using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：径向模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】单 pass，无中间缓冲：
    ///   source → 目标缓冲（pass 0）
    ///   _Params = (BlurRadius * 0.02, Iteration, RadialCenterX, RadialCenterY)
    ///   模糊向量是 UV 空间量，**与分辨率无关**，故本效果不依赖链工作缓冲尺寸。
    ///
    /// 【URP14 适配】
    ///   - 本效果无临时 RT，因此不涉及 RTHandle 生命周期问题。
    ///   - 保留源项目的 Adaptive Performance 否决逻辑（URPAdaptivePerformance.GetRadialBlurEnable）。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "径向模糊 (Radial Blur)")]
    public class RadialBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 1f);
        public IntParameter Iteration = new ClampedIntParameter(10, 2, 30);
        public FloatParameter RadialCenterX = new ClampedFloatParameter(0.5f, 0f, 1f);
        public FloatParameter RadialCenterY = new ClampedFloatParameter(0.5f, 0f, 1f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/RadialBlur";
        }
    }


    public class RadialBlurRenderer : BlurVolumeRenderer<RadialBlur>
    {
        private const string PROFILER_TAG = "RadialBlur";

        static class ShaderIDs
        {
            internal static readonly int Params = Shader.PropertyToID("_Params");
        }
        public override bool IsActive()
        {
            bool isActive = base.IsActive();
            var hasValue = URPAdaptivePerformance.GetRadialBlurEnable(isActive, out var result);
            if (hasValue && !result)
            {
                return false;
            }
            return isActive;
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            cmd.BeginSample(PROFILER_TAG);

            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector4(settings.BlurRadius.value * 0.02f, settings.Iteration.value, settings.RadialCenterX.value, settings.RadialCenterY.value));

            // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
            //   （显式 pass index），不用三参数形式。
            //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
            //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
            //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
            //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
            //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
            //   pass index 与源项目语义保持一致。
            cmd.Blit(source, target, m_BlitMaterial, 0);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
