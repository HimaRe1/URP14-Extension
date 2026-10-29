using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


namespace XPostProcessing
{
    [VolumeComponentMenu(VolumeDefine.Glitch + "RGB颜色分离V5 (RGB SplitV5)")]
    public class GlitchRGBSplitV5 : VolumeSetting
    {
        public override bool IsActive() => Amplitude.value > 0;
        public FloatParameter Amplitude = new ClampedFloatParameter(0f, 0f, 5f);
        public FloatParameter Speed = new ClampedFloatParameter(0.1f, 0f, 1f);
        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Glitch/RGBSplitV5";
        }

    }


    public sealed class GlitchRGBSplitV5Renderer : VolumeRenderer<GlitchRGBSplitV5>
    {
        private const string PROFILER_TAG = "GlitchRGBSplitV5";
        private Texture2D m_NoiseTex;

        // *URP14Port-Phase3-B07FIX：源项目此字段即“只赋值不使用”（与源文件逐字节一致），
        // 编译产生 CS0414。行为零影响，按保真原则保留字段，仅压制警告。
#pragma warning disable 0414
        private float TimeX = 1.0f;
#pragma warning restore 0414


        public override bool Init()
        {
            bool successs = base.Init();
            if (successs)
            {
                m_NoiseTex = Resources.Load("X-Noise256") as Texture2D;
            }
            return successs;
        }

        static class ShaderIDs
        {
            internal static readonly int NoiseTex = Shader.PropertyToID("_NoiseTex");
            internal static readonly int Params = Shader.PropertyToID("_Params");
        }


        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            // if (!settings.IsActive())
            //     return;


            cmd.BeginSample(PROFILER_TAG);

            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector2(settings.Amplitude.value, settings.Speed.value));
            if (m_NoiseTex != null)
            {
                m_BlitMaterial.SetTexture(ShaderIDs.NoiseTex, m_NoiseTex);
            }

            cmd.Blit(source, target, m_BlitMaterial);
            // cmd.Blit(target, source);

            cmd.EndSample(PROFILER_TAG);
        }
    }

}