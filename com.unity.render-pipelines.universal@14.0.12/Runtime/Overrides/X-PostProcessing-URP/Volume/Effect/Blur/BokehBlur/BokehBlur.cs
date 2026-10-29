using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：散景模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   1. 链缓冲降采样拷贝进 _BufferRT1
    ///   2. 单 pass：黄金角螺旋采样（_GoldenRot / _Params），_BufferRT1 → 目标缓冲
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///     —— 该尺寸同时用于中间缓冲的像素尺寸与 _Params.zw（像素步长）。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "散景模糊 (Bokeh Blur)")]
    public class BokehBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 3f);
        public IntParameter Iteration = new ClampedIntParameter(32, 8, 128);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(2f, 1f, 10f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/BokehBlur";
        }
    }

    public class BokehBlurRenderer : BlurVolumeRenderer<BokehBlur>
    {
        private const string PROFILER_TAG = "BokehBlur";

        private Vector4 mGoldenRot = new Vector4();

        private const int k_Buffer1 = 0;

        public override bool Init()
        {
            bool sc = base.Init();
            if (sc)
            {
                // Precompute rotations
                float c = Mathf.Cos(2.39996323f);
                float s = Mathf.Sin(2.39996323f);
                mGoldenRot.Set(c, s, -s, c);
            }
            return sc;
        }

        static class ShaderIDs
        {
            internal static readonly int GoldenRot = Shader.PropertyToID("_GoldenRot");
            internal static readonly int Params = Shader.PropertyToID("_Params");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            cmd.BeginSample(PROFILER_TAG);

            Vector2Int chain = ChainSize;
            // *URP14Port-Phase3-B04D：模糊偏移的分母 = **链工作缓冲分辨率**（不是输出分辨率）。
            //   源项目用 Screen.width/height 作分母，而源项目 renderScale=1 ⇒ 链缓冲宽 == Screen.width
            //   ⇒ offset 在链缓冲上正好等于「radius 个 texel」。
            //   D1-a 的 renderScale=0.25 使链缓冲只有输出的 1/4（160×90），
            //   若仍用输出分辨率作分母，模糊强度会缩水到 1/4 ⇒ 效果几乎不可见。
            //   ⇒ 与 Batch 04-C 的 _ScreenParams → _ScaledScreenParams 属同一类修正：
            //   「以被采样缓冲的 texel 为单位」的量必须按**链工作缓冲**分辨率换算。
                        int rtWidth = Mathf.Max(1, (int)(chain.x / settings.RTDownScaling.value));
            int rtHeight = Mathf.Max(1, (int)(chain.y / settings.RTDownScaling.value));

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_BokehBlur_RT1");

            // downsample screen copy into smaller RT
            BlurUtility.CopyTexture(cmd, source, buffer1);

            m_BlitMaterial.SetVector(ShaderIDs.GoldenRot, mGoldenRot);
            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector4(settings.Iteration.value, settings.BlurRadius.value, 1f / chain.x, 1f / chain.y));
            // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
            //   （显式 pass index），不用三参数形式。
            //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
            //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
            //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
            //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
            //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
            //   pass index 与源项目语义保持一致。
            cmd.Blit(buffer1, target, m_BlitMaterial, 0);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
