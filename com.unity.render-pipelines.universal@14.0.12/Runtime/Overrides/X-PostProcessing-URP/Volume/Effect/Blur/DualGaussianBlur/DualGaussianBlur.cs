using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：双重高斯模糊（Mip 金字塔，源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）—— 独立的 Mip 金字塔策略，不套用普通 Blur 的双缓冲实现】
    ///   下采样链（第 i 层尺寸 = 第 0 层尺寸 / 2^i）：
    ///     source → _BlurMipDownH{i}（水平模糊, pass 0）→ _BlurMipDownV{i}（垂直模糊, pass 0）
    ///   上采样链（从最深层往回，逐层放大到该层的尺寸）：
    ///     _BlurMipDownV{最深层} → _BlurMipUpH{i}（水平, pass 0）→ _BlurMipUpV{i}（垂直, pass 0）
    ///   最终：_BlurMipUpV{0} → 目标缓冲（pass 1 = FragCombine）
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每层 4 个独立 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - **逐级 1/2 尺寸关系必须保留**（源算法的核心），故使用固定尺寸 RTHandle 而非自动缩放 RTHandle。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "双重高斯模糊 (Dual Gaussian Blur)")]
    public class DualGaussianBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 15f);
        public IntParameter Iteration = new ClampedIntParameter(4, 1, 8);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 10f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/DualGaussianBlur";
        }
    }

    public class DualGaussianBlurRenderer : BlurVolumeRenderer<DualGaussianBlur>
    {
        private const string PROFILER_TAG = "DualGaussianBlur";

        /// <summary>源项目的金字塔层数上限（ClampedIntParameter 上限为 8，16 为历史预留）。</summary>
        private const int k_MaxPyramidSize = 16;

        // 每层 4 个缓冲的编号偏移（与源项目 down_vertical / down_horizontal / up_vertical / up_horizontal 一一对应）
        private const int k_DownVertical = 0;
        private const int k_DownHorizontal = 1;
        private const int k_UpVertical = 2;
        private const int k_UpHorizontal = 3;
        private const int k_BuffersPerLevel = 4;

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

            // *URP14Port-Phase3-B04D FIX-08：每趟 Blit 使用**独立的材质实例**。
            //   同一 CommandBuffer 内复用同一材质时，材质常量在 GPU 执行期才上传，
            //   ⇒ 所有 Blit 都会读到最后一次写入的值（末值覆盖）。
            //   源项目即存在该问题（水平趟实际拿到垂直偏移），本批修正之。
            Material matH = GetSlotMaterial(0);
            Material matV = GetSlotMaterial(1);

            // Downsample
            RenderTargetIdentifier lastDown = source;
            for (int i = 0; i < iteration; i++)
            {
                int baseIndex = i * k_BuffersPerLevel;
                RTHandle mipDownV = EnsureBuffer(baseIndex + k_DownVertical, tw, th, "_XPP_DualGaussianBlur_MipDownV" + i);
                RTHandle mipDownH = EnsureBuffer(baseIndex + k_DownHorizontal, tw, th, "_XPP_DualGaussianBlur_MipDownH" + i);
                RTHandle mipUpV = EnsureBuffer(baseIndex + k_UpVertical, tw, th, "_XPP_DualGaussianBlur_MipUpV" + i);
                RTHandle mipUpH = EnsureBuffer(baseIndex + k_UpHorizontal, tw, th, "_XPP_DualGaussianBlur_MipUpH" + i);

                // horizontal blur
                matH.SetVector(ShaderIDs.BlurOffset, new Vector4(settings.BlurRadius.value / chain.x, 0, 0, 0));
                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(lastDown, mipDownH, matH, 0);

                // vertical blur
                matV.SetVector(ShaderIDs.BlurOffset, new Vector4(0, settings.BlurRadius.value / chain.y, 0, 0));
                cmd.Blit(mipDownH, mipDownV, matV, 0);

                lastDown = mipDownV;
                tw = Mathf.Max(tw / 2, 1);
                th = Mathf.Max(th / 2, 1);
            }

            // Upsample
            RTHandle lastUp = m_Buffers[(iteration - 1) * k_BuffersPerLevel + k_DownVertical];
            for (int i = iteration - 2; i >= 0; i--)
            {
                int baseIndex = i * k_BuffersPerLevel;
                RTHandle mipUpV = m_Buffers[baseIndex + k_UpVertical];
                RTHandle mipUpH = m_Buffers[baseIndex + k_UpHorizontal];

                // horizontal blur
                matH.SetVector(ShaderIDs.BlurOffset, new Vector4(settings.BlurRadius.value / chain.x, 0, 0, 0));
                cmd.Blit(lastUp, mipUpH, matH, 0);

                // vertical blur
                matV.SetVector(ShaderIDs.BlurOffset, new Vector4(0, settings.BlurRadius.value / chain.y, 0, 0));
                cmd.Blit(mipUpH, mipUpV, matV, 0);

                lastUp = mipUpV;
            }

            // Render blurred texture in blend pass
            cmd.Blit(lastUp, target, matV, 1);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
