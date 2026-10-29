using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace XPostProcessing
{
    [VolumeComponentMenu(VolumeDefine.Environment + "顶面积雪 (SurfaceSnow)")]
    public class SurfaceSnow : VolumeSetting
    {
        public override bool IsActive() => snowStrength.value > 0;

        [Tooltip("积雪强度")]
        public ClampedFloatParameter snowStrength = new ClampedFloatParameter(0, 0, 1);
        [Tooltip("积雪贴图")]
        public TextureParameter snowTexture = new TextureParameter(null);
        [Tooltip("倾斜控制")]
        public ClampedFloatParameter snowBias = new ClampedFloatParameter(0.8f, 0, 1);
        [Tooltip("积雪贴图尺寸")]
        public FloatParameter snowSize = new FloatParameter(1);
        public ClampedFloatParameter debugValue = new ClampedFloatParameter(1, 0, 10);
        public IntParameter sampleOffset = new IntParameter(0);
        public MinIntParameter blurInt = new MinIntParameter(1, 1);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/SurfaceSnow";
        }
    }

    public class SurfaceSnowRenderer : VolumeRenderer<SurfaceSnow>
    {

        private const string PROFILER_TAG = "Surface Snow";

        /// <summary>
        /// *URP14Port-Phase3-B04E FIX-02：积雪遮罩中间缓冲。
        ///   源项目用 RenderTexture.GetTemporary(Screen.width / blurInt, ...) + ReleaseTemporary
        ///   申请/释放临时 RT，与 URP14 的 RTHandle 体系互不相通（Batch 04-D 同类问题）。
        ///   改为 Renderer 持有的持久 RTHandle，按尺寸变化重分配、Cleanup() 释放
        ///   （经 BlurUtility.EnsureBuffer / ReleaseBuffer，与 Blur 家族同一套适配模式）。
        /// </summary>
        private RTHandle m_SnowBuffer;

        static class ShaderContants
        {
            public static readonly int snowTextureID = Shader.PropertyToID("_SnowTexture");
            public static readonly int snowStrengthID = Shader.PropertyToID("_SnowStrength");
            public static readonly int snowBiasID = Shader.PropertyToID("_SnowBias");
            public static readonly int snowTexelSizeID = Shader.PropertyToID("_TexelSize");
            public static readonly int snowSizeID = Shader.PropertyToID("_SnowSize");
            public static readonly int debugValueID = Shader.PropertyToID("_DebugValue");
            public static readonly int sampleOffsetID = Shader.PropertyToID("_SampleOffset");
            public static readonly int snowBlurTexID = Shader.PropertyToID("_SnowBlurTex");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null) return;

            cmd.BeginSample(PROFILER_TAG);

            // *URP14Port-Phase3-B04E FIX-01：_TexelSize 与中间缓冲尺寸的分母 = 链工作缓冲分辨率。
            //   源项目用 Screen.width/height；D1-a renderScale=0.25 下链缓冲只有输出的 1/4，
            //   「以被采样缓冲的 texel 为单位」的量必须按链工作分辨率换算（同 04-D FIX-01）。
            //   中间缓冲尺寸 = 链缓冲 / blurInt，保留源算法的降采样比例关系。
            Vector2Int chain = BlurUtility.GetChainSize(ChainDescriptor);

            Vector2 texelSize;
            texelSize.x = (1f / chain.x);
            texelSize.y = (1f / chain.y);
            m_BlitMaterial.SetVector(ShaderContants.snowTexelSizeID, texelSize);
            m_BlitMaterial.SetTexture(ShaderContants.snowTextureID, settings.snowTexture.value);
            m_BlitMaterial.SetFloat(ShaderContants.snowStrengthID, settings.snowStrength.value);
            m_BlitMaterial.SetFloat(ShaderContants.snowBiasID, settings.snowBias.value);
            m_BlitMaterial.SetFloat(ShaderContants.snowSizeID, settings.snowSize.value);
            m_BlitMaterial.SetFloat(ShaderContants.debugValueID, settings.debugValue.value);
            m_BlitMaterial.SetInt(ShaderContants.sampleOffsetID, settings.sampleOffset.value);

            int bw = Mathf.Max(1, chain.x / settings.blurInt.value);
            int bh = Mathf.Max(1, chain.y / settings.blurInt.value);
            var desc = BlurUtility.MakeBufferDescriptor(ChainDescriptor, bw, bh);
            BlurUtility.EnsureBuffer(ref m_SnowBuffer, desc, "_XPP_SurfaceSnow_Buffer");

            cmd.Blit(source, m_SnowBuffer, m_BlitMaterial, 0);

            m_BlitMaterial.SetTexture(ShaderContants.snowBlurTexID, m_SnowBuffer);

            cmd.Blit(source, target, m_BlitMaterial, 1);
            //cmd.Blit(target,source);

            cmd.EndSample(PROFILER_TAG);
        }

        public override void Cleanup()
        {
            BlurUtility.ReleaseBuffer(ref m_SnowBuffer);
            base.Cleanup();
        }
    }

}
