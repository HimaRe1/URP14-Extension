using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
// #if SOUL_ENGINE
// using SoulEngine;
// #endif

namespace XPostProcessing
{
    [VolumeComponentMenu(VolumeDefine.Extra + "探测波 RaderWave")]
    public class RaderWave : VolumeSetting
    {
        public override bool IsActive() => raderWaveStrenght.value > 0;

        [Tooltip("特效强度")]
        public ClampedFloatParameter raderWaveStrenght = new ClampedFloatParameter(0, 0, 1);
        [Tooltip("探测波颜色")]
        public ColorParameter raderWaveColor = new ColorParameter(Color.white);
        [Tooltip("探测范围颜色")]
        public ColorParameter raderAreaColor = new ColorParameter(Color.blue);
        //[Tooltip("探测距离")]
        //public MinFloatParameter raderWaveMaxDistance = new MinFloatParameter(40,0);
        [Tooltip("探测宽度")]
        public ClampedFloatParameter raderWaveWidth = new ClampedFloatParameter(1, 0, 5);

        [Tooltip("传递速度")]
        public MinFloatParameter raderWaveSpeed = new MinFloatParameter(1, 0);
        [Tooltip("传递时间")]
        public MinFloatParameter raderWaveTime = new MinFloatParameter(10, 0);

        private Vector4 m_OriginPos;
        public Vector4 OriginPos { get { return m_OriginPos; } set { m_OriginPos = value; m_TimeReset = true; } }
        private bool m_TimeReset;
        public bool TimeReset { get { return m_TimeReset; } set { m_TimeReset = value; } }

        /// <summary>
        /// *URP14Port-Phase3-B04E FIX-03 开关：
        ///   true  = 返回修正后的 shader 名（推荐）
        ///   false = 复刻源项目（返回 "RaderWav"，与 shader 声明的 "RaderWave" 不匹配）
        ///
        /// 【源项目缺陷（第 5 处）】源项目 GetShaderName 返回 "Hidden/PostProcessing/RaderWav"，
        ///   而 shader 文件声明的是 "Hidden/PostProcessing/RaderWave"（少了 'e'）⇒
        ///   Shader.Find 失败 ⇒ Init 不创建材质 ⇒ IsActive 恒 false ⇒
        ///   **该效果在源项目中从未渲染过任何内容**（实测 URP14，renderCount=0 且
        ///   IsActive 的 base 分支因 m_BlitMaterial==null 返回 false）。
        ///   本批修正使效果按其参数/Shader 的设计语义工作。
        /// </summary>
        private static readonly bool k_FixShaderName = true;

        public override string GetShaderName()
        {
            return k_FixShaderName ? "Hidden/PostProcessing/RaderWave" : "Hidden/PostProcessing/RaderWav";
        }
    }


    public class RaderWaveRenderer : VolumeRenderer<RaderWave>
    {

        private float timeCount = 0;
        private const string PROFILER_TAG = "Rader Wave";

        public override bool IsActive()
        {
            if (settings.TimeReset)
            {
                timeCount = 0;
                settings.TimeReset = false;
            }

            if (timeCount > settings.raderWaveTime.value)
            {
                return false;
            }
            else
            {
                timeCount += Time.deltaTime;
            }
            return base.IsActive();
        }


        static class ShaderContants
        {
            public static readonly int raderOriginPosID = Shader.PropertyToID("_OriginPosition");
            public static readonly int raderWaveColorID = Shader.PropertyToID("_WaveColor");
            public static readonly int raderMaxDistanceID = Shader.PropertyToID("_MaxDistance");
            public static readonly int raderWaveWidthID = Shader.PropertyToID("_RaderWaveWidth");
            public static readonly int raderWaveAreaColorID = Shader.PropertyToID("_RaderAreaColor");
        }

        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null) return;



            cmd.BeginSample(PROFILER_TAG);
            // #if SOUL_ENGINE
            // 		    // IKernel kernel = GameWorld.gameKernel;
            // 			// Vector4 vec = LocalHelper.GetPosition(kernel,GameWorld.gameKernel.GetMainPlayer());
            // 			// vec.w = timeCount * settings.raderWaveSpeed.value;
            // 			// m_BlitMaterial.SetVector(ShaderContants.raderOriginPosID,vec);
            // #endif
            Vector4 vec = settings.OriginPos;
            vec.w = timeCount * settings.raderWaveSpeed.value;
            m_BlitMaterial.SetVector(ShaderContants.raderOriginPosID, vec);
            m_BlitMaterial.SetVector(ShaderContants.raderWaveColorID, settings.raderWaveColor.value);
            //m_BlitMaterial.SetFloat(ShaderContants.raderMaxDistanceID,settings.raderWaveMaxDistance.value);
            m_BlitMaterial.SetFloat(ShaderContants.raderWaveWidthID, settings.raderWaveWidth.value);
            m_BlitMaterial.SetVector(ShaderContants.raderWaveAreaColorID, settings.raderAreaColor.value);

            cmd.Blit(source, target, m_BlitMaterial);
            //cmd.Blit(target,source);

            cmd.EndSample(PROFILER_TAG);
        }
    }



}
