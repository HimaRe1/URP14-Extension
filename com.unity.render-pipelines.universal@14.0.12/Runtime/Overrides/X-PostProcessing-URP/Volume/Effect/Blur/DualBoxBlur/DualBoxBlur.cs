using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：双重方框模糊（Mip 金字塔，源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）—— 独立的 Mip 金字塔策略】
    ///   下采样链：source → _BlurMipDown{i}（pass 0 = FragBoxBlur），逐层尺寸减半
    ///   上采样链：_BlurMipDown{最深层} → _BlurMipUp{i}（pass 0），逐层放大回该层尺寸
    ///   最终：_BlurMipUp{0} → 目标缓冲（pass 1 = FragCombine）
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每层 2 个独立 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - **逐级 1/2 尺寸关系必须保留**，故使用固定尺寸 RTHandle。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "双重方框模糊 (Dual Box Blur)")]
    public class DualBoxBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 15f);
        public IntParameter Iteration = new ClampedIntParameter(4, 1, 8);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 10f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/DualBoxBlur";
        }
    }

    public class DualBoxBlurRenderer : BlurVolumeRenderer<DualBoxBlur>
    {
        private const string PROFILER_TAG = "DualBoxBlur";

        private const int k_MaxPyramidSize = 16;

        private const int k_Down = 0;
        private const int k_Up = 1;
        private const int k_BuffersPerLevel = 2;

        static class ShaderIDs
        {
            internal static readonly int BlurOffset = Shader.PropertyToID("_BlurOffset");
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
                        int tw = Mathf.Max(1, (int)(chain.x / settings.RTDownScaling.value));
            int th = Mathf.Max(1, (int)(chain.y / settings.RTDownScaling.value));

            int iteration = Mathf.Clamp(settings.Iteration.value, 1, k_MaxPyramidSize);

            // 源项目在下采样前先设置一次 _BlurOffset，此后整个金字塔链共用该偏移（不再逐 pass 改写）。
            // *URP14Port-Phase3-B04D FIX-08：down 与 up 链用不同材质实例（防末值覆盖）。
            Material matDown = GetSlotMaterial(0);
            Material matUp = GetSlotMaterial(1);

            matDown.SetVector(ShaderIDs.BlurOffset, new Vector4(settings.BlurRadius.value / (float)chain.x, settings.BlurRadius.value / (float)chain.y, 0, 0));

            // Downsample
            RenderTargetIdentifier lastDown = source;
            for (int i = 0; i < iteration; i++)
            {
                int baseIndex = i * k_BuffersPerLevel;
                RTHandle mipDown = EnsureBuffer(baseIndex + k_Down, tw, th, "_XPP_DualBoxBlur_MipDown" + i);
                RTHandle mipUp = EnsureBuffer(baseIndex + k_Up, tw, th, "_XPP_DualBoxBlur_MipUp" + i);

                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(lastDown, mipDown, matDown, 0);

                lastDown = mipDown;
                tw = Mathf.Max(tw / 2, 1);
                th = Mathf.Max(th / 2, 1);
            }

            // Upsample
            RTHandle lastUp = m_Buffers[(iteration - 1) * k_BuffersPerLevel + k_Down];
            for (int i = iteration - 2; i >= 0; i--)
            {
                RTHandle mipUp = m_Buffers[i * k_BuffersPerLevel + k_Up];
                cmd.Blit(lastUp, mipUp, matUp, 0);
                lastUp = mipUp;
            }

            // Render blurred texture in blend pass
            cmd.Blit(lastUp, target, matUp, 1);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
