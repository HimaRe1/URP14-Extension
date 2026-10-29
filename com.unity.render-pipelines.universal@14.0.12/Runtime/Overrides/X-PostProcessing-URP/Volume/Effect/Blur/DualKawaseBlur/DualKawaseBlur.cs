using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：双重 Kawase 模糊（Mip 金字塔，源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   下采样链：source → _BlurMipDown{i}（pass 0 = Vert_DownSample/Frag_DownSample），逐层尺寸减半
    ///   上采样链：_BlurMipDown{最深层} → _BlurMipUp{i}，逐层放大回该层尺寸
    ///   最终：_BlurMipUp{0} → 目标缓冲（pass 1 = Vert_UpSample/Frag_UpSample）
    ///
    /// 【URP14 适配 + 源项目缺陷修正】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每层 2 个独立 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - **逐级 1/2 尺寸关系必须保留**，故使用固定尺寸 RTHandle。
    ///   - FIX-02（源项目缺陷）：源项目 C# 写的是 `_BlurOffset`，而 DualKawaseBlur.shader
    ///     实际读取的 uniform 是 `_Offset` → 源项目的抽头扩散倍数恒为 1（即 1 + _Offset），
    ///     BlurRadius 完全不起作用。
    ///   - FIX-03（源项目缺陷）：源项目的**上采样链误用了 pass 0（DownSample）**，
    ///     而 shader 专门提供了 pass 1（UpSample，8 抽头 tent），且最终 blit 用的正是 pass 1。
    ///     本批把上采样链改为 pass 1，使金字塔的上行阶段真正使用 tent 上采样。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "双重Kawase模糊 (Dual Kawase Blur)")]
    public class DualKawaseBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 15f);
        public IntParameter Iteration = new ClampedIntParameter(4, 1, 8);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(2f, 1f, 10f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/DualKawaseBlur";
        }
    }

    public class DualKawaseBlurRenderer : BlurVolumeRenderer<DualKawaseBlur>
    {
        private const string PROFILER_TAG = "DualKawaseBlur";

        /// <summary>
        /// *URP14Port-Phase3-B04D FIX-02 开关：true = 写 `_Offset`（修正源项目缺陷），false = 复刻源项目。
        /// </summary>
        private static readonly bool k_FixOffsetBinding = true;

        /// <summary>
        /// *URP14Port-Phase3-B04D FIX-03 开关：true = 上采样链使用 pass 1（UpSample，修正源项目缺陷），
        /// false = 复刻源项目（pass 0）。
        /// </summary>
        private static readonly bool k_FixUpSamplePass = true;

        private const int k_MaxPyramidSize = 16;

        private const int k_Down = 0;
        private const int k_Up = 1;
        private const int k_BuffersPerLevel = 2;

        static class ShaderIDs
        {
            // shader 实际读取的 uniform
            internal static readonly int Offset = Shader.PropertyToID("_Offset");
            // 源项目 C# 实际写入的属性（保留用于复刻源行为）
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

            // *URP14Port-Phase3-B04D FIX-08：down（pass 0）与 up（pass 1）用不同材质实例，
            //   避免同一材质复用导致的末值覆盖。
            Material matDown = GetSlotMaterial(0);
            Material matUp = GetSlotMaterial(1);

            if (k_FixOffsetBinding)
            {
                matDown.SetFloat(ShaderIDs.Offset, settings.BlurRadius.value);
                matUp.SetFloat(ShaderIDs.Offset, settings.BlurRadius.value);
            }
            else
            {
                matDown.SetFloat(ShaderIDs.BlurOffset, settings.BlurRadius.value);
                matUp.SetFloat(ShaderIDs.BlurOffset, settings.BlurRadius.value);
            }

            // Downsample
            RenderTargetIdentifier lastDown = source;
            for (int i = 0; i < iteration; i++)
            {
                int baseIndex = i * k_BuffersPerLevel;
                RTHandle mipDown = EnsureBuffer(baseIndex + k_Down, tw, th, "_XPP_DualKawaseBlur_MipDown" + i);
                RTHandle mipUp = EnsureBuffer(baseIndex + k_Up, tw, th, "_XPP_DualKawaseBlur_MipUp" + i);

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
                cmd.Blit(lastUp, mipUp, k_FixUpSamplePass ? matUp : matDown, k_FixUpSamplePass ? 1 : 0);
                lastUp = mipUp;
            }

            // Render blurred texture in blend pass
            cmd.Blit(lastUp, target, matUp, 1);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
