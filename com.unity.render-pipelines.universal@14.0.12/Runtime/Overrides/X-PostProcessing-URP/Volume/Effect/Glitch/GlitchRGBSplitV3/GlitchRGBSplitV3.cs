using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


namespace XPostProcessing
{
    [VolumeComponentMenu(VolumeDefine.Glitch + "RGB颜色分离V3 (RGB SplitV3)")]
    public class GlitchRGBSplitV3 : VolumeSetting
    {
        public override bool IsActive() => Frequency.value > 0;

        public DirectionEXParameter SplitDirection = new DirectionEXParameter(DirectionEX.Horizontal);
        public IntervalTypeParameter intervalType = new IntervalTypeParameter(IntervalType.Random);
        public FloatParameter Frequency = new ClampedFloatParameter(0f, 0.1f, 25f);
        public FloatParameter Amount = new ClampedFloatParameter(30f, 0f, 200f);
        public FloatParameter Speed = new ClampedFloatParameter(20f, 0f, 20f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Glitch/RGBSplitV3";
        }

    }


    public sealed class GlitchRGBSplitV3Renderer : VolumeRenderer<GlitchRGBSplitV3>
    {
        private const string PROFILER_TAG = "GlitchRGBSplitV3";


        private float randomFrequency;
        private int frameCount = 0;



     

        static class ShaderIDs
        {
            internal static readonly int Params = Shader.PropertyToID("_Params");
        }


        public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (m_BlitMaterial == null)
                return;

            cmd.BeginSample(PROFILER_TAG);

            UpdateFrequency(settings);

            m_BlitMaterial.SetVector(ShaderIDs.Params, new Vector3(settings.intervalType.value == IntervalType.Random ? randomFrequency : settings.Frequency.value, settings.Amount.value, settings.Speed.value));

            // *URP14Port-Phase3-B04D1 FIX-01：把 SplitDirection 作为**显式 pass index** 传入。
            //
            // 【根因（实证，见 BATCH-04D1-REPORT §三）】
            //   实测（URP14 / 2022.3.62f2）：`cmd.Blit(src, dst, mat)`（三参数）渲染的是材质的
            //   **最后一个 Pass**（而非 Pass 0）—— 用 2 Pass 判别 shader（pass0=红 / pass1=绿）
            //   测得三参数输出为绿色，四参数 Blit(...,0) 输出为红色。
            //   本 shader 有 3 个 Pass：
            //       0 = Frag_Horizontal（横向分离）
            //       1 = Frag_Vertical（纵向分离）
            //       2 = Frag_Horizontal_Vertical（横纵分离）
            //   ⇒ 三参数形式下，无论 SplitDirection 取何值，输出的都是 Pass 2（横纵分离）。
            //   实证：三种 SplitDirection 取值在"竖直条纹"与"水平条纹"两种探针下的输出
            //         逐位相同（diff 均 0.427277 / 0.422075）⇒ 参数完全无效。
            //
            // 【为什么这样修】
            //   · `DirectionEX` 枚举 {Horizontal=0, Vertical=1, Horizontal_Vertical=2}
            //     与三个 Pass 的顺序**一一对应**，直接传 `(int)SplitDirection.value` 即为正确的 Pass；
            //   · 库内同类效果（GlitchScanLineJitter / GlitchScreenJump / GlitchScreenShake /
            //     GlitchTileJitter / GlitchWaveJitter / RapidVignette* 共 7 个）均采用
            //     "方向/类型参数 → 显式 pass index"的写法，本效果是唯一漏掉的；
            //   · 该缺陷**继承自源项目**（URP12 的同一行也是三参数、同样未使用 SplitDirection）。
            //
            // 【行为变化】默认 SplitDirection=Horizontal ⇒ 由"横纵分离"变为"横向分离"，
            //   即恢复参数声明本身的含义。
            cmd.Blit(source, target, m_BlitMaterial, (int)settings.SplitDirection.value);

            cmd.EndSample(PROFILER_TAG);
        }

        void UpdateFrequency(GlitchRGBSplitV3 settings)
        {
            if (settings.intervalType.value == IntervalType.Random)
            {
                if (frameCount > settings.Frequency.value)
                {

                    frameCount = 0;
                    randomFrequency = UnityEngine.Random.Range(0, settings.Frequency.value);
                }
                frameCount++;
            }

            if (settings.intervalType.value == IntervalType.Infinite)
            {
                m_BlitMaterial.EnableKeyword("USING_Frequency_INFINITE");
            }
            else
            {
                m_BlitMaterial.DisableKeyword("USING_Frequency_INFINITE");
            }
        }
    }

}