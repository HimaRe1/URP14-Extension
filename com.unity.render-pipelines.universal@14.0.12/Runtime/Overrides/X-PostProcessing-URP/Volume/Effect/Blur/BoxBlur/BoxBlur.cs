using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：方框模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   1. 链缓冲降采样拷贝进 _BufferRT1
    ///   2. 迭代 Iteration 次：RT1→RT2、RT2→RT1（乒乓），每次都用同一个双向 UV 偏移
    ///   3. 最终把 RT1 用 pass 0 输出到目标缓冲
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "方框模糊 (Box Blur)")]
    public class BoxBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 5f);
        public IntParameter Iteration = new ClampedIntParameter(6, 1, 20);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 8f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/BoxBlur";
        }
    }

    public class BoxBlurRenderer : BlurVolumeRenderer<BoxBlur>
    {
        private const string PROFILER_TAG = "BoxBlur";

        private const int k_Buffer1 = 0;
        private const int k_Buffer2 = 1;

        static class ShaderIDs
        {
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

            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_BoxBlur_RT1");
            RTHandle buffer2 = EnsureBuffer(k_Buffer2, rtWidth, rtHeight, "_XPP_BoxBlur_RT2");

            // downsample screen copy into smaller RT
            BlurUtility.CopyTexture(cmd, source, buffer1);

            // *URP14Port-Phase3-B04D：源项目在此处有一个 `if (settings.Iteration.value > 20) return;` 守卫。
            // 该守卫不可达（Iteration 是 ClampedIntParameter(6, 1, 20)，上限 20），
            // 且一旦触发会跳过最终 Blit（目标缓冲不写入）与 cmd.EndSample（profiler 采样不配对）。
            // 本批移除该不可达守卫，行为与源项目在可达输入下完全一致。
            Material matH = GetSlotMaterial(0);
            Material matV = GetSlotMaterial(1);

            for (int i = 0; i < settings.Iteration.value; i++)
            {
                Vector4 BlurRadius = new Vector4(settings.BlurRadius.value / (float)chain.x, settings.BlurRadius.value / (float)chain.y, 0, 0);

                // RT1 -> RT2
                matH.SetVector(ShaderIDs.BlurRadius, BlurRadius);
                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(buffer1, buffer2, matH, 0);

                // RT2 -> RT1
                matV.SetVector(ShaderIDs.BlurRadius, BlurRadius);
                cmd.Blit(buffer2, buffer1, matV, 0);
            }

            // Render blurred texture in blend pass
            // *URP14Port-Phase3-B04D：保留源行为（3 参 Blit = pass 0，FragBoxBlur；shader 里的
            // FragCombine(pass 1) 在源项目同样未被使用）。
            cmd.Blit(buffer1, target, matV, 0);

            cmd.EndSample(PROFILER_TAG);
        }
    }
}
