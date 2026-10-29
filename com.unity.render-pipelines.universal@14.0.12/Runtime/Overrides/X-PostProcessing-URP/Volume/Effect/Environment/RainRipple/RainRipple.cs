using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    [VolumeComponentMenu(VolumeDefine.Environment + "雨涟漪 (Rain Ripple)")]
    public class RainRippel : VolumeSetting
    {

        public override bool IsActive() => rippleStrength.value > 0;

        [Tooltip("降雨密度")]
        public ClampedFloatParameter rainIntensity = new ClampedFloatParameter(1, 0, 1);
        [Tooltip("生效距离")]
        public FloatParameter rainEnableDistance = new FloatParameter(150);
        [Tooltip("涟漪强度")]
        public ClampedFloatParameter rippleStrength = new ClampedFloatParameter(0f, 0, 2);
        [Tooltip("涟漪模拟贴图")]
        public TextureParameter rippleMaskTexture = new TextureParameter(null);
        [Tooltip("涟漪大小")]
        public ClampedFloatParameter rippleSize = new ClampedFloatParameter(1, 0, 1);
        [Tooltip("涟漪频率")]
        public FloatParameter rippleFrequency = new FloatParameter(1);

        [Tooltip("角度控制")]
        public ClampedFloatParameter rippleBias = new ClampedFloatParameter(0.8f, 0, 1);


        [Tooltip("水流显示强度")]
        public ClampedFloatParameter flowStrength = new ClampedFloatParameter(0f, 0, 1);
        [Tooltip("墙面水流图")]
        public TextureParameter flowMap = new TextureParameter(null);

        [Tooltip("水流流速,Map采样偏移")]
        public FloatParameter flowSpeed = new FloatParameter(1);
        [Tooltip("墙面水流扰动法线图")]
        public TextureParameter flowNormalMap = new TextureParameter(null);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/RainRipple";
        }

    }

    public class RainRippleRenderer : VolumeRenderer<RainRippel>
    {

        private const string PROFILER_TAG = "Rain Ripple";


        static class ShaderContants
        {
            public static readonly int rainRippleStrengthID = Shader.PropertyToID("_RippleStrength");
            public static readonly int rainRippleMaskTextureID = Shader.PropertyToID("_RippleTexture");
            public static readonly int texelSizeID = Shader.PropertyToID("_TexelSize");
            public static readonly int rainRippleDiffusionFrequencyID = Shader.PropertyToID("_RippleDiffusionFrequency");
            public static readonly int rainRippleSizeID = Shader.PropertyToID("_RippleSize");
            public static readonly int rainIntensityID = Shader.PropertyToID("_RainIntensity");
            public static readonly int rainRippleBiasID = Shader.PropertyToID("_RippleBias");
            public static readonly int rainFlowMapID = Shader.PropertyToID("_FlowDownTexture");
            public static readonly int rainFlowNormalID = Shader.PropertyToID("_FlowDownNormal");
            public static readonly int rainFlowSpeedID = Shader.PropertyToID("_FlowSpeed");
            public static readonly int rainFlowStrengthID = Shader.PropertyToID("_FlowStrength");

            public static readonly int rainEnableDistanceID = Shader.PropertyToID("_RainEnableDistance");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;



            cmd.BeginSample(PROFILER_TAG);

            // *URP14Port-Phase3-B04E FIX-01：_TexelSize 的分母必须是「链工作缓冲分辨率」而非输出分辨率。
            //   源项目用 1/Screen.width、1/Screen.height。该 texel 尺寸在 shader 中用于
            //   GetWorldNormalBYDepth 的四邻域深度采样（重建场景法线）——被采样的是
            //   _CameraDepthTexture，其分辨率 = 链工作缓冲分辨率（D1-a renderScale=0.25 下为 160×90）。
            //   源项目 renderScale=1 时 Screen.width == 链缓冲宽 ⇒ 恰好等于「1 个深度 texel」；
            //   D1-a 的 0.25 使链缓冲只有输出的 1/4 ⇒ 若仍用输出分辨率作分母，邻域偏移只有
            //   0.25 个深度 texel，法线重建会被深度纹理的双线性采样抹平。
            //   与 Batch 04-D 的模糊偏移分母（FIX-01）属同一类修正，同为 C# 侧。
            Vector2Int chain = BlurUtility.GetChainSize(ChainDescriptor);
            Vector2 texelS;
            texelS.x = 1f / chain.x;
            texelS.y = 1f / chain.y;
            m_BlitMaterial.SetTexture(ShaderContants.rainRippleMaskTextureID, settings.rippleMaskTexture.value);
            m_BlitMaterial.SetTexture(ShaderContants.rainFlowMapID, settings.flowMap.value);
            m_BlitMaterial.SetTexture(ShaderContants.rainFlowNormalID, settings.flowNormalMap.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainRippleStrengthID, settings.rippleStrength.value);
            m_BlitMaterial.SetVector(ShaderContants.texelSizeID, texelS);
            m_BlitMaterial.SetFloat(ShaderContants.rainRippleDiffusionFrequencyID, settings.rippleFrequency.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainRippleSizeID, settings.rippleSize.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainIntensityID, settings.rainIntensity.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainRippleBiasID, settings.rippleBias.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainFlowStrengthID, settings.flowStrength.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainFlowSpeedID, settings.flowSpeed.value);
            m_BlitMaterial.SetFloat(ShaderContants.rainEnableDistanceID, settings.rainEnableDistance.value);

            cmd.Blit(source, target, m_BlitMaterial);
            // cmd.Blit(target, source);

            cmd.EndSample(PROFILER_TAG);
        }
    }

}
