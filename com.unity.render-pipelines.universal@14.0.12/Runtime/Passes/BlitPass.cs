using System;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// *URP14Port-D1a 实验用最小版 BlitPass。
    ///
    /// 职责：把 back buffer（场景，渲染分辨率）放大 blit 到 front buffer（UI 输出分辨率），
    /// 然后把 back buffer 与深度附件也切到 UI 输出分辨率并 Swap ——
    /// 使后续的 Overlay UI 相机画在全分辨率缓冲上。
    ///
    /// 这是 URP12 魔改 BlitPass 的 URP14 等价实现。
    ///
    /// *URP14Port-Phase3-B07（Gamma2Line 链，补齐 Batch 02 暂缓项）：
    /// 新增 BlitColorTransform 模式（对齐 URP12 魔改 BlitPass）。
    ///   · Upscale（默认，原有行为）：场景 back -> UI 分辨率 front 的放大 blit + resize + swap；
    ///   · Gamma2Line：同尺寸 back -> front blit，期间启用 _SRGB_TO_LINEAR_CONVERSION
    ///     把「UI 相机 gamma 空间合成」的缓冲还原成 Linear，然后**无条件 Swap** ——
    ///     与 URP12 魔改 BlitPass「结尾无条件 SwapColorBuffer」的次数语义逐位对齐
    ///     （URP12 UniversalRenderer.cs:1046 注释：'这次插桩同时也补齐了 swap 次数'）。
    ///     转换走 CoreBlit 的 BilinearDebugDraw pass（FragmentURPBlit 支持 sRGB 关键字；
    ///     默认 Pass 0 的 FragNearest 是 core 包代码、无转换分支）。
    ///
    /// 关键：内容并不是靠"resize 时保留"活下来的，而是靠
    ///   ReSizeFrontBuffer(UI 输出) -> Blit(back→front，放大) -> ReSizeBackBufferAndSave -> Swap
    /// 这个**分步**顺序把内容从 back 搬到了 front。URP12 原版同理。
    ///
    /// ⚠️ URP14 与 URP12 最大的差异是 RTHandle 生命周期：
    /// ReAllocateIfNeeded 会**立即**把旧 RTHandle 放入 stale pool，池满则直接 Release。
    /// 因此必须先把 blit 命令提交给 ScriptableRenderContext，再动 back buffer，
    /// 否则 GPU 还没执行的 blit source 可能已被 CPU 侧释放。
    /// 本实现固定为两个阶段提交，见 Execute()。
    /// </summary>
    public class BlitPass : ScriptableRenderPass
    {
        Material m_BlitMaterial;

        RenderTextureDescriptor m_TargetDesc;

        /// <summary>*URP14Port-Phase3-B07：本次 Enqueue 的 blit 模式（对齐 URP12 魔改 BlitColorTransform）。</summary>
        public enum BlitColorTransform
        {
            Upscale,
            Gamma2Line,
            // *URP14Port-Phase3-B07-fix：源 BlitPass.cs:19-24 三模式补齐。
            // Line2Gamma = Upscale blit 期间做 Linear→SRGB（去程编码），与 UI 相机的
            // Gamma2Line（回程解码）构成 sRGB 往返 —— 源 UR:1219/1255 在
            // sIsGammaCorrectEnable=true 时 Upscale 即用此模式。缺它则 Gamma2Line
            // 会把线性场景值多解码一次 ⇒ 全屏变暗。
            Line2Gamma,
        }

        BlitColorTransform m_ColorTransform = BlitColorTransform.Upscale;

        // FragmentURPBlit 所在 pass（支持 sRGB 转换关键字）；Pass 0 的 FragNearest 无转换分支。
        int m_ConvertPass = -1;
        // *URP14Port-Phase3-B07FIX（上采样滤波）：NearestDebugDraw = PointClamp 采样 + 转换关键字
        //（CoreBlit.shader:335-347），供 upscalingFilter=Point(NN) 时使用。
        int m_NearestConvertPass = -1;
        // *URP14Port-Phase3-B07FIX（上采样滤波）：rs<1 且 upscalingFilter 解析为 Point（NN）时为真，
        // Upscale/Line2Gamma 的 blit 据此选最近邻采样。此前硬编码导致 NN/Bilinear 切换无效。
        bool m_PointSampling;

        static readonly ProfilingSampler m_ProfilingSamplerBlit = new ProfilingSampler("D1a.BlitUpscale");
        static readonly ProfilingSampler m_ProfilingSamplerResize = new ProfilingSampler("D1a.ResizeAndSwap");
        static readonly ProfilingSampler m_ProfilingSamplerGamma = new ProfilingSampler("D1a.Gamma2Line");

        public BlitPass(RenderPassEvent evt, Material blitMaterial)
        {
            base.profilingSampler = new ProfilingSampler(nameof(BlitPass));
            m_BlitMaterial = blitMaterial;
            renderPassEvent = evt;
            base.useNativeRenderPass = false;
        }

        /// <summary>
        /// 传入 UI 相机的最终输出 descriptor。该 descriptor 由 UniversalRenderPipeline 在渲染前统一
        /// 计算，Fix6、ResizeDepth、颜色 Buffer 与这里共用同一来源，不再使用 Screen.width/height。
        /// *URP14Port-Phase3-B07FIX（上采样滤波）：pointSampling = rs<1 且 upscalingFilter 解析为
        /// Point（NN，Auto 已由 URP.cs:1269 解析为实际滤波）——blit pass 据此选最近邻/双线性采样。
        /// </summary>
        public void Setup(RenderTextureDescriptor targetDesc,
            BlitColorTransform colorTransform = BlitColorTransform.Upscale,
            bool pointSampling = false)
        {
            m_TargetDesc = targetDesc;
            m_ColorTransform = colorTransform;
            m_PointSampling = pointSampling;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_BlitMaterial == null)
            {
                Debug.LogErrorFormat("Missing {0}. {1} render pass will not execute. Check for missing reference in the renderer resources.", m_BlitMaterial, GetType().Name);
                return;
            }

            var renderer = renderingData.cameraData.renderer as UniversalRenderer;
            if (renderer == null)
                return;

            if (m_ColorTransform == BlitColorTransform.Gamma2Line)
            {
                ExecuteGamma2Line(context, renderer);
                return;
            }

            var colorBuffer = renderer.m_ColorBufferSystem;

            bool needChangeSize = RenderTargetBufferSystem.Desc.width != m_TargetDesc.width
                                  || RenderTargetBufferSystem.Desc.height != m_TargetDesc.height;

            // ---------- 阶段一：把场景内容从 back 搬到已放大的 front ----------
            var cmd = CommandBufferPool.Get("D1a Blit Upscale");
            using (new ProfilingScope(cmd, m_ProfilingSamplerBlit))
            {
                // 1) front 先切到 UI 输出尺寸（back 仍是渲染分辨率，场景内容完好）
                if (needChangeSize)
                    colorBuffer.ReSizeFrontBuffer(cmd, m_TargetDesc);

                // 2) 放大 blit：back(渲染分辨率) -> front(UI 输出)。
                RTHandle source = colorBuffer.PeekBackBuffer();
                RTHandle dest = colorBuffer.PeekFrontBuffer();
                // *URP14Port-Phase3-B07FIX（上采样滤波）：按 upscalingFilter 选采样
                //（NN=PointClamp、Linear/Bilinear=LinearClamp；Auto 已由 URP.cs:1269 解析为实际滤波）。
                // gamma 链开（Line2Gamma）时转换关键字只在两个 DebugDraw pass 有效：
                //   NN   → NearestDebugDraw（PointClamp + 关键字，shader:335-347）
                //   线性 → BilinearDebugDraw（LinearClamp + 关键字，shader:315-327）
                // gamma 关（Upscale）：NN 走 Pass 0 FragNearest（原生最近邻、无关键字），
                //   线性走 BilinearDebugDraw（无关键字）——此前硬编码导致 NN/Bilinear 切换无效。
                // FSR 源项目即未放行（fix8 R2），按线性兜底（面板提示维持不变）。
                if (m_ConvertPass < 0)
                    m_ConvertPass = m_BlitMaterial.FindPass("BilinearDebugDraw");
                if (m_NearestConvertPass < 0)
                    m_NearestConvertPass = m_BlitMaterial.FindPass("NearestDebugDraw");
                bool encode = m_ColorTransform == BlitColorTransform.Line2Gamma;
                int blitPassIndex = m_PointSampling
                    ? (encode ? m_NearestConvertPass : 0)
                    : m_ConvertPass;
                if (encode)
                {
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SRGBToLinearConversion, false);
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LinearToSRGBConversion, true);
                }
                Blitter.BlitCameraTexture(cmd, source, dest,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_BlitMaterial, blitPassIndex);
                if (encode)
                {
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SRGBToLinearConversion, false);
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LinearToSRGBConversion, false);
                }
            }
            // 提交阶段一：此后 back 的旧内容不再被任何未执行的命令引用，才允许重建它。
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // ---------- 阶段二：back / 深度切到 UI 输出尺寸 ----------
            if (needChangeSize)
            {
                var cmd2 = CommandBufferPool.Get("D1a Resize And Swap");
                using (new ProfilingScope(cmd2, m_ProfilingSamplerResize))
                {
                    colorBuffer.ReSizeBackBufferAndSave(cmd2, m_TargetDesc);
                    renderer.ResizeDepth(cmd2, m_TargetDesc);
                }
                context.ExecuteCommandBuffer(cmd2);
                CommandBufferPool.Release(cmd2);
            }

            // *URP14Port-Phase3-B07FIX：源 BlitPass.cs:98-103 —— SwapColorBuffer 在
            // needChangeSize 之外**无条件**执行。rs=1（无需扩容）时也必须切到承载了
            // Line2Gamma 编码结果的 front buffer：不换则编码结果被丢弃在无人读取的
            // front 上，Gamma2Line 会对仍是 Linear 的场景再解码一次 ⇒ 场景变暗
            //（用户实测：gamma 开 + rs=1 时 3D 场景变深变暗）。
            var cmd3 = CommandBufferPool.Get("D1a Swap");
            renderer.SwapColorBuffer(cmd3);
            context.ExecuteCommandBuffer(cmd3);
            CommandBufferPool.Release(cmd3);
        }

        /// <summary>
        /// *URP14Port-Phase3-B07：Gamma2Line 执行体。
        /// back（gamma 空间合成）-> front，启用 _SRGB_TO_LINEAR_CONVERSION，
        /// 然后**无条件 Swap**（URP12 魔改的 swap 次数语义）。
        /// 不做 resize：UI 相机阶段缓冲尺寸已由基底相机的 Upscale blit 统一到 UI 输出分辨率。
        /// </summary>
        void ExecuteGamma2Line(ScriptableRenderContext context, UniversalRenderer renderer)
        {
            var colorBuffer = renderer.m_ColorBufferSystem;

            var cmd = CommandBufferPool.Get("D1a Gamma2Line");
            using (new ProfilingScope(cmd, m_ProfilingSamplerGamma))
            {
                if (m_ConvertPass < 0)
                    m_ConvertPass = m_BlitMaterial.FindPass("BilinearDebugDraw");

                RTHandle source = colorBuffer.PeekBackBuffer();
                RTHandle dest = colorBuffer.PeekFrontBuffer();
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SRGBToLinearConversion, true);
                Blitter.BlitCameraTexture(cmd, source, dest,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_BlitMaterial, m_ConvertPass);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SRGBToLinearConversion, false);
            }
            // 与 Upscale 模式相同的安全性约束：blit 引用 back/front 两个缓冲，
            // 必须先提交命令，再做任何可能触发 ReAllocate/Release 的操作（此处无 resize）。
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            var cmd2 = CommandBufferPool.Get("D1a Gamma2Line Swap");
            renderer.SwapColorBuffer(cmd2);
            context.ExecuteCommandBuffer(cmd2);
            CommandBufferPool.Release(cmd2);
        }
    }
}
