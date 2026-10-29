using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    public enum TiltShiftBlurQualityLevel
    {
        High_Quality = 0,
        Normal_Quality = 1,
    }

    [Serializable]
    public sealed class TiltShiftBlurQualityLevelParameter : VolumeParameter<TiltShiftBlurQualityLevel> { public TiltShiftBlurQualityLevelParameter(TiltShiftBlurQualityLevel value, bool overrideState = false) : base(value, overrideState) { } }

    /// <summary>
    /// *URP14Port-Phase3-B04D：移轴模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   1. source → _BufferRT1（pass = QualityLevel：0=DiscKernel_HQ，1=DiscKernel_LQ）
    ///      该 pass 的 alpha 通道写入"离焦遮罩"（TiltShiftMask）
    ///   2. cmd.SetGlobalTexture(_BlurredTex, _BufferRT1) —— 把模糊结果作为**全局纹理**暴露
    ///   3. source → 目标缓冲（pass 2 = FragBlend）：按 _BlurredTex.a 在原图与模糊图之间插值
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - _BlurredTex 仍以**全局纹理**方式绑定，但源从"每帧临时 RT"改为"跨帧持久的 RTHandle"，
    ///     避免源项目那种"全局纹理指向已释放临时 RT"的悬空风险。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "移轴模糊 (Tilt Shift Blur)")]
    public class TiltShiftBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public TiltShiftBlurQualityLevelParameter QualityLevel = new TiltShiftBlurQualityLevelParameter(TiltShiftBlurQualityLevel.High_Quality);
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 1f);
        public FloatParameter AreaSize = new ClampedFloatParameter(0.5f, 0f, 1f);
        public IntParameter Iteration = new ClampedIntParameter(2, 1, 8);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 2f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/TiltShiftBlur";
        }
    }

    public class TiltShiftBlurRenderer : BlurVolumeRenderer<TiltShiftBlur>
    {
        private const string PROFILER_TAG = "TiltShiftBlur";

        private const int k_Buffer1 = 0;

        /// <summary>
        /// *URP14Port-Phase3-B04D FIX-06 开关：
        ///   true  = 按 shader 的真实遮罩标定传递 AreaSize（修正源项目缺陷，推荐）
        ///   false = 复刻源项目（直接传 AreaSize，遮罩上限仅 0.001 → 效果几乎不可见）
        ///
        /// 【源项目缺陷】
        ///   TiltShiftBlur.shader 的 TiltShiftMask 为 `abs(centerY * _BlurSize * 0.001)`，
        ///   而 AreaSize 的取值范围是 [0,1] ⇒ 遮罩最大值仅 0.001；
        ///   FragBlend 用 `saturate(blurredColor.a)` 作为混合权重 ⇒ 权重 ≤ 0.1%，
        ///   **源项目的 TiltShiftBlur 在视觉上等同于没有生效**。
        ///   本批在 C# 侧把 AreaSize 乘以 1000（shader 不动，保持逐字节一致），
        ///   使遮罩 = |centerY| * AreaSize ⇒ AreaSize=1 时在画面上下边缘饱和到 1.0，
        ///   即"中间带清晰、上下虚化"的移轴语义。
        /// </summary>
        private static readonly bool k_FixMaskScale = true;

        /// <summary>遮罩标定补偿系数（见 FIX-06 说明）。</summary>
        private const float k_MaskScaleCompensation = 1000f;

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

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_TiltShiftBlur_RT1");

            // Set Property
            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector2(
                k_FixMaskScale ? settings.AreaSize.value * k_MaskScaleCompensation : settings.AreaSize.value,
                settings.BlurRadius.value));

            // Do Blit
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

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
