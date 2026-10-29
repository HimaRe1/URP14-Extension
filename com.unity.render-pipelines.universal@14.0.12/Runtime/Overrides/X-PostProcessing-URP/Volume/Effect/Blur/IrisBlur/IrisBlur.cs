using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    public enum IrisBlurQualityLevel
    {
        High_Quality = 0,
        Normal_Quality = 1,
    }

    [Serializable]
    public sealed class IrisBlurQualityLevelParameter : VolumeParameter<IrisBlurQualityLevel> { public IrisBlurQualityLevelParameter(IrisBlurQualityLevel value, bool overrideState = false) : base(value, overrideState) { } }

    /// <summary>
    /// *URP14Port-Phase3-B04D：光圈模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   Iteration == 1：
    ///     source → _BufferRT1（pass = QualityLevel）→ 全局 _BlurredTex → source → 目标（pass 2 = FragBlend）
    ///   Iteration &gt; 1：
    ///     source → _BufferRT1 → _BufferRT2 → _BufferRT1 …（乒乓 Iteration 次，pass = QualityLevel）
    ///     → 全局 _BlurredTex → source → 目标（pass 2 = FragBlend）
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - FIX-05（URP14 API 差异）：源项目的 `settings.Iteration == 1` 在 URP14 **无法编译** ——
    ///     core 14.0.12 的 VolumeParameter&lt;T&gt; 只提供 `explicit operator T`，
    ///     IntParameter 也没有 `operator ==(int)`。本批改为 `settings.Iteration.value == 1`。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "光圈模糊 (Iris Blur)")]
    public class IrisBlur : VolumeSetting
    {
        public override bool IsActive() => AreaSize.value > 0;

        public IrisBlurQualityLevelParameter QualityLevel = new IrisBlurQualityLevelParameter(IrisBlurQualityLevel.Normal_Quality);
        public FloatParameter AreaSize = new ClampedFloatParameter(0f, 0f, 1f);

        [Range(0.0f, 1.0f)]
        public FloatParameter BlurRadius = new ClampedFloatParameter(1.0f, 0f, 1f);

        [Range(1, 8)]
        public IntParameter Iteration = new ClampedIntParameter(2, 1, 8);

        [Range(1, 2)]
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1.0f, 1.0f, 2.0f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/IrisBlur";
        }

    }

    public sealed class IrisBlurRenderer : BlurVolumeRenderer<IrisBlur>
    {
        private const string PROFILER_TAG = "IrisBlur";

        /// <summary>
        /// *URP14Port-Phase3-B04D FIX-05 开关：
        ///   true  = 按 shader 的真实遮罩标定传递 AreaSize（修正源项目缺陷，推荐）
        ///   false = 复刻源项目（直接传 AreaSize，遮罩上限仅 0.002 → 效果几乎不可见）
        ///
        /// 【源项目缺陷】
        ///   IrisBlur.shader 的 IrisMask 为 `dot(center,center) * (_BluSize * 0.001)`，
        ///   其中 center = uv*2-1 ⇒ dot ∈ [0,2]。而 AreaSize 的取值范围是 [0,1]，
        ///   ⇒ 遮罩最大值仅 2*1*0.001 = 0.002。
        ///   FragBlend 用 `saturate(blurredColor.a)` 作为混合权重 ⇒ 权重 ≤ 0.2%，
        ///   **源项目的 IrisBlur 在视觉上等同于没有生效**。
        ///   本批在 C# 侧把 AreaSize 乘以 1000（shader 不动，保持逐字节一致），
        ///   使遮罩 = dot * AreaSize ⇒ AreaSize=1 时在画面四角饱和到 1.0，
        ///   即"中心清晰、边缘虚化"的光圈语义。
        /// </summary>
        private static readonly bool k_FixMaskScale = true;

        /// <summary>遮罩标定补偿系数（见 FIX-05 说明）。</summary>
        private const float k_MaskScaleCompensation = 1000f;

        private const int k_Buffer1 = 0;
        private const int k_Buffer2 = 1;

        static class ShaderIDs
        {
            internal static readonly int Params = Shader.PropertyToID("_Params");
            internal static readonly int BlurredTex = Shader.PropertyToID("_BlurredTex");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            cmd.BeginSample(PROFILER_TAG);

            // *URP14Port-Phase3-B04D FIX-05：URP14 的 VolumeParameter<T> 只有显式转换，必须取 .value
            if (settings.Iteration.value == 1)
            {
                HandleOneBlitBlur(cmd, source, target);
            }
            else
            {
                HandleMultipleIterationBlur(cmd, source, target, settings.Iteration.value);
            }

            cmd.EndSample(PROFILER_TAG);
        }

        void HandleOneBlitBlur(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
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

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_IrisBlur_RT1");

            // Set Property
            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector4(
                k_FixMaskScale ? settings.AreaSize.value * k_MaskScaleCompensation : settings.AreaSize.value,
                settings.BlurRadius.value));

            // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
            //   （显式 pass index），不用三参数形式。
            //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
            //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
            //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
            //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
            //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
            //   pass index 与源项目语义保持一致。
            cmd.Blit(source, buffer1, m_BlitMaterial, (int)settings.QualityLevel.value);

            // Final Blit
            cmd.SetGlobalTexture(ShaderIDs.BlurredTex, buffer1.nameID);
            cmd.Blit(source, target, m_BlitMaterial, 2);
        }

        void HandleMultipleIterationBlur(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target, int Iteration)
        {
            Vector2Int chain = ChainSize;
            int rtWidth = Mathf.Max(1, (int)(chain.x / settings.RTDownScaling.value));
            int rtHeight = Mathf.Max(1, (int)(chain.y / settings.RTDownScaling.value));

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_IrisBlur_RT1");
            RTHandle buffer2 = EnsureBuffer(k_Buffer2, rtWidth, rtHeight, "_XPP_IrisBlur_RT2");

            // Set Property
            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector2(
                k_FixMaskScale ? settings.AreaSize.value * k_MaskScaleCompensation : settings.AreaSize.value,
                settings.BlurRadius.value));

            // 源项目的乒乓顺序：第 1 次写入 _BufferRT1，第 2 次写入 _BufferRT2，之后交替。
            RTHandle finalBlur = buffer1;
            RenderTargetIdentifier first = source;
            RenderTargetIdentifier second = buffer1;
            bool secondIsBuffer1 = true;

            for (int i = 0; i < Iteration; i++)
            {
                // Do Blit
                cmd.Blit(first, second, m_BlitMaterial, (int)settings.QualityLevel.value);

                finalBlur = secondIsBuffer1 ? buffer1 : buffer2;
                first = second;
                secondIsBuffer1 = !secondIsBuffer1;
                second = secondIsBuffer1 ? buffer1 : buffer2;
            }

            // Final Blit
            cmd.SetGlobalTexture(ShaderIDs.BlurredTex, finalBlur.nameID);
            cmd.Blit(source, target, m_BlitMaterial, 2);
        }
    }
}
