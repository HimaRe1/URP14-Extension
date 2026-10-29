using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：高斯模糊（源项目 URP12 X-PostProcessing 的 Blur 家族）。
    ///
    /// 【源算法（URP12）】
    ///   1. 把链缓冲降采样拷贝进 _BufferRT1（尺寸 = Screen / RTDownScaling）
    ///   2. 迭代 Iteration 次：水平模糊 RT1→RT2，垂直模糊 RT2→RT1（乒乓）
    ///   3. 最终把 RT1 模糊 pass 0 输出到目标缓冲
    ///
    /// 【URP14 适配】
    ///   - cmd.GetTemporaryRT/ReleaseTemporaryRT → 每个 Renderer 持有自己的 RTHandle（Cleanup 释放）
    ///   - Screen.width/height → 链工作缓冲尺寸（cameraTargetDescriptor，已含 renderScale 缩放）
    /// </summary>
    [VolumeComponentMenu(VolumeDefine.Blur + "高斯模糊 (Gaussian Blur)")]
    public class GaussianBlur : VolumeSetting
    {
        public override bool IsActive() => BlurRadius.value > 0;
        public FloatParameter BlurRadius = new ClampedFloatParameter(0f, 0f, 5f);
        public IntParameter Iteration = new ClampedIntParameter(6, 1, 15);
        public FloatParameter RTDownScaling = new ClampedFloatParameter(1f, 1f, 8f);

        public override string GetShaderName()
        {
            return "Hidden/PostProcessing/Blur/GaussianBlur";
        }
    }

    public class GaussianBlurRenderer : BlurVolumeRenderer<GaussianBlur>
    {
        private const string PROFILER_TAG = "GaussianBlur";

        // 缓冲编号：0 = _BufferRT1，1 = _BufferRT2（与源项目的 nameID 对应关系保持一致）
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

            
            RTHandle buffer1 = EnsureBuffer(k_Buffer1, rtWidth, rtHeight, "_XPP_GaussianBlur_RT1");
            RTHandle buffer2 = EnsureBuffer(k_Buffer2, rtWidth, rtHeight, "_XPP_GaussianBlur_RT2");

            // downsample screen copy into smaller RT
            BlurUtility.CopyTexture(cmd, source, buffer1);

            // *URP14Port-Phase3-B04D FIX-08：水平/垂直两趟必须用**不同的材质实例**。
            //   实测（URP14 / 2022.3.62f2）：同一 CommandBuffer 内用同一个材质做多次 Blit 时，
            //   材质常量在 GPU 执行期才上传 ⇒ **所有 Blit 都会读到最后一次写入的值**（末值覆盖）。
            //   ⇒ 源项目的水平趟实际拿到了垂直偏移，模糊方向完全错误。
            //   本批用 slot 材质为每趟分配独立实例，恢复"每趟各用自己的偏移"的算法语义。
            Material matH = GetSlotMaterial(0);
            Material matV = GetSlotMaterial(1);

            for (int i = 0; i < settings.Iteration.value; i++)
            {
                // horizontal blur
                matH.SetVector(ShaderIDs.BlurRadius, new Vector4(settings.BlurRadius.value / chain.x, 0, 0, 0));
                // *URP14Port-Phase3-B04D FIX-09：本效果的所有 Blit 均使用**四参数**形式
                //   （显式 pass index），不用三参数形式。
                //   原因：实测（URP14 / 2022.3.62f2）对**多 Pass** shader，
                //   `cmd.Blit(src, dst, mat)`（三参数）**不会把材质常量写入 GPU**
                //   ⇒ uniform 恒为 0 ⇒ 效果彻底静默失效；
                //   `cmd.Blit(src, dst, mat, pass)`（四参数）100% 正常。
                //   本效果的 shader 是多 Pass（Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 必须用四参数。
                //   pass index 与源项目语义保持一致。
                cmd.Blit(buffer1, buffer2, matH, 0);


                // vertical blur
                matV.SetVector(ShaderIDs.BlurRadius, new Vector4(0, settings.BlurRadius.value / chain.y, 0, 0));
                cmd.Blit(buffer2, buffer1, matV, 0);
            }

            // Render blurred texture in blend pass
            // *URP14Port-Phase3-B04D：保留源行为 —— 源项目此处用的是 3 参 Blit（= pass 0，FragGaussianBlur），
            // 而不是 shader 里预留的 FragCombine(pass 1)。即最终输出会再叠加一次（最后一次设置的）高斯核。
            // 该行为在源项目即已存在（注释写的是 "blend pass" 但实际未用 pass 1），本批按"保留源行为"处理。

            cmd.Blit(buffer1, target, matV, 0);


            cmd.EndSample(PROFILER_TAG);
        }
    }
}
