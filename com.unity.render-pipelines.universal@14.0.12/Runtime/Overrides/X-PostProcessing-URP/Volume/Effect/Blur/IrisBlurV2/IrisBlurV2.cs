using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：光圈模糊 V2（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】单 pass，无中间缓冲：
    ///   source → 目标缓冲（pass 0 = 黄金角螺旋散景；pass 1 = FragPreview，仅显示遮罩）
    ///   _Gradient = (centerOffsetX, centerOffsetY, AreaSize * 0.1)
    ///   _Params   = (Iteration, BlurRadius, 1/W, 1/H)
    ///
    /// 【URP14 适配】
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - 本效果无临时 RT，因此不涉及 RTHandle 生命周期问题。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "光圈模糊V2 (Iris BlurV2)")]
    public class IrisBlurV2 : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;

        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 3f);
        public IntParameter Iteration = new ClampedIntParameter(60, 8, 128);
        public FloatParameter centerOffsetX = new ClampedFloatParameter(0f, -1f, 1f);
        public FloatParameter centerOffsetY = new ClampedFloatParameter(0f, -1f, 1f);
        public FloatParameter AreaSize = new ClampedFloatParameter(8f, 0f, 50f);
        public BoolParameter showPreview = new BoolParameter(false);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/IrisBlurV2";
        }

    }

    public sealed class IrisBlurV2Renderer : BlurVolumeRenderer<IrisBlurV2>
    {
        private const string PROFILER_TAG = "IrisBlurV2";

        private Vector4 mGoldenRot = new Vector4();

        public override bool Init()
        {
            bool success = base.Init();
            if (success)
            {
                float c = Mathf.Cos(2.39996323f);
                float s = Mathf.Sin(2.39996323f);
                mGoldenRot.Set(c, s, -s, c);
            }
            return success;
        }

        static class ShaderIDs
        {
            internal static readonly int GoldenRot = Shader.PropertyToID("_GoldenRot");
            internal static readonly int Gradient = Shader.PropertyToID("_Gradient");
            internal static readonly int Params = Shader.PropertyToID("_Params");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            cmd.BeginSample(PROFILER_TAG);

            Vector2Int chain = ChainSize;

            
            m_BlitMaterial.SetVector(ShaderIDs.GoldenRot, mGoldenRot);
            m_BlitMaterial.SetVector(ShaderIDs.Gradient, new Vector3(settings.centerOffsetX.value, settings.centerOffsetY.value, settings.AreaSize.value * 0.1f));
            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector4(settings.Iteration.value, settings.BlurRadius.value, 1f / chain.x, 1f / chain.y));

            // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
            //   （显式 pass index），不用三参数形式。
            //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
            //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
            //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
            //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
            //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
            //   pass index 与源项目语义保持一致。
            cmd.Blit(source, target, m_BlitMaterial, settings.showPreview.value ? 1 : 0);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
