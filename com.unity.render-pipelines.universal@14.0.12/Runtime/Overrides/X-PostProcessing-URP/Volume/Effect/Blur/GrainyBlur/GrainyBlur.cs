using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：粒状模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   RTDownScaling &gt; 1 时：source → _BufferRT（降采样拷贝）→ 目标缓冲（pass 0）
    ///   RTDownScaling == 1 时：source → 目标缓冲（pass 0），无中间缓冲
    ///   _Params = (BlurRadius / Screen.height, Iteration)
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.height → 链工作缓冲高度（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - 源项目在 RTDownScaling == 1 时仍无条件执行 ReleaseTemporaryRT（释放一个从未申请的 nameID），
    ///     在 URP12 的临时池语义下无害；本批改为"按需持有 RTHandle"，无需显式释放。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "粒状模糊 (Grainy Blur)")]
    public class GrainyBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 50f);
        public IntParameter Iteration = new ClampedIntParameter(4, 1, 8);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 10f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/GrainyBlur";
        }
    }

    public class GrainyBlurRenderer : BlurVolumeRenderer<GrainyBlur>
    {
        private const string PROFILER_TAG = "GrainyBlur";

        private const int k_Buffer = 0;

        static class ShaderIDs
        {
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
                        bool useIntermediate = settings.RTDownScaling.value > 1f;

            RTHandle buffer = null;
            if (useIntermediate)
            {
                int rtWidth = Mathf.Max(1, (int)(chain.x / settings.RTDownScaling.value));
                int rtHeight = Mathf.Max(1, (int)(chain.y / settings.RTDownScaling.value));
                buffer = EnsureBuffer(k_Buffer, rtWidth, rtHeight, "_XPP_GrainyBlur_RT");

                // downsample screen copy into smaller RT
                BlurUtility.CopyTexture(cmd, source, buffer);
            }

            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector2(settings.BlurRadius.value / chain.y, settings.Iteration.value));

            if (useIntermediate)
            {
                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(buffer, target, m_BlitMaterial, 0);
            }
            else
            {
                cmd.Blit(source, target, m_BlitMaterial, 0);
            }

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
