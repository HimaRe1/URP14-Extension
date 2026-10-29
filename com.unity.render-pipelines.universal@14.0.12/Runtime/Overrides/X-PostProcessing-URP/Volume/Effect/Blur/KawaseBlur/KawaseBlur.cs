using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：Kawase 模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   1. 链缓冲降采样拷贝进 _BufferRT1
    ///   2. 迭代 Iteration 次：单抽头 Kawase 模糊，RT1↔RT2 乒乓，
    ///      每次的像素偏移 = i / RTDownScaling + BlurRadius（逐次增大）
    ///   3. 最终把当前缓冲用 pass 0 输出到目标缓冲（偏移 = Iteration / RTDownScaling + BlurRadius）
    ///
    /// 【URP14 适配 + 源项目缺陷修正】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    ///   - FIX-01（源项目缺陷）：源项目 C# 写的是 `_BlurOffset`（ShaderIDs.BlurRadius），
    ///     而 KawaseBlur.shader 实际读取的 uniform 是 `_Offset` → 源项目的偏移恒为 0，
    ///     模糊半径实际为 0（只剩 ±0.5 texel 的 2×2 平均），效果几乎不可见。
    ///     本批按 shader 的真实语义改为写 `_Offset`，使 BlurRadius 生效。
    ///     若需与源项目像素级一致，把下面的 k_FixOffsetBinding 置 false 即可回到源行为。
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "Kawase模糊 (Kawase Blur)")]
    public class KawaseBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 5f);
        public IntParameter Iteration = new ClampedIntParameter(6, 1, 15);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 8f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/KawaseBlur";
        }
    }

    public class KawaseBlurRenderer : BlurVolumeRenderer<KawaseBlur>
    {
        private const string PROFILER_TAG = "KawaseBlur";

        /// <summary>
        /// *URP14Port-Phase3-B04D FIX-01 开关：
        ///   true  = 按 shader 真实语义写 `_Offset`（BlurRadius 生效，修正源项目缺陷，推荐）
        ///   false = 完全复刻源项目（写 `_BlurOffset`，shader 读到的 `_Offset` 恒为 0）
        /// </summary>
        private static readonly bool k_FixOffsetBinding = true;

        private const int k_Buffer1 = 0;
        private const int k_Buffer2 = 1;

        static class ShaderIDs
        {
            // shader 实际读取的 uniform
            internal static readonly int Offset = Shader.PropertyToID("_Offset");
            // 源项目 C# 实际写入的属性（保留用于复刻源行为）
            internal static readonly int BlurRadius = Shader.PropertyToID("_BlurOffset");
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

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_KawaseBlur_RT1");
            RTHandle buffer2 = EnsureBuffer(k_Buffer2, rtWidth, rtHeight, "_XPP_KawaseBlur_RT2");

            // downsample screen copy into smaller RT
            BlurUtility.CopyTexture(cmd, source, buffer1);

            // *URP14Port-Phase3-B04D FIX-08：每趟的 _Offset 递增，必须各用独立材质实例
            //   （同一材质复用 ⇒ 末值覆盖，所有趟都拿到最后一个 Offset）。
            bool needSwitch = true;
            for (int i = 0; i < settings.Iteration.value; i++)
            {
                float offset = i / settings.RTDownScaling.value + settings.BlurRadius.value;
                Material mat = GetSlotMaterial(i);
                if (k_FixOffsetBinding)
                    mat.SetFloat(ShaderIDs.Offset, offset);
                else
                    mat.SetFloat(ShaderIDs.BlurRadius, offset);

                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(needSwitch ? buffer1 : buffer2, needSwitch ? buffer2 : buffer1, mat, 0);
                needSwitch = !needSwitch;
            }

            float finalOffset = settings.Iteration.value / settings.RTDownScaling.value + settings.BlurRadius.value;
            Material matFinal = GetSlotMaterial(settings.Iteration.value);
            if (k_FixOffsetBinding)
                matFinal.SetFloat(ShaderIDs.Offset, finalOffset);
            else
                matFinal.SetFloat(ShaderIDs.BlurRadius, finalOffset);

            cmd.Blit(needSwitch ? buffer1 : buffer2, target, matFinal, 0);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
