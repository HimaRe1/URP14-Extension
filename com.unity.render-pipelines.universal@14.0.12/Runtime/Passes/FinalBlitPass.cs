using System;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Copy the given color target to the current camera target
    ///
    /// You can use this pass to copy the result of rendering to
    /// the camera target. The pass takes the screen viewport into
    /// consideration.
    /// </summary>
    public class FinalBlitPass : ScriptableRenderPass
    {
        RTHandle m_Source;
        private PassData m_PassData;
        // *URP14Port-Phase3-B01（UI Gamma 链 4/4）：
        // true  = 缓冲是 Linear，按标准做一次 LinearToSRGB（非 UI 相机，与迁移前一致）；
        // false = 缓冲存的是 gamma 值，禁止再编码，原样输出以保留 UI 的 gamma 空间结果。
        // 注：URP12 用「SRGBToLinear+LinearToSRGB 互逆恒等」达到同一效果；因 coreBlitPS 已支持
        // _LINEAR_TO_SRGB_CONVERSION，直接关掉它即可等价，无需新增 shader 关键字。
        bool m_IsLine = true;

        // *URP14Port-Phase3-B07FIX-AndroidSrgb：缓冲在本 pass 之前是否已还原为 Linear
        // （= gamma 开时 Gamma2Line 回程已跑，UniversalRenderer 传入 uiBufferBackToLinear）。
        // 安卓 GLES/Vulkan 的 backbuffer 没有硬件 sRGB 编码，且 Display.main.requiresSrgbBlitToBackbuffer
        // 在部分设备返回 False（OnePlus KB2000 / Android 14 实测），requireSrgbConversion 判据失效
        // ⇒ 缓冲为 Linear 时必须由本 pass 关键字补上最后一次编码，否则 linear 直出（全屏过暗）。
        bool m_BufferIsLinear = false;

        // Use specialed URP fragment shader pass for debug draw support and color space conversion/encoding support.
        // See CoreBlit.shader and BlitHDROverlay.shader
        static class BlitPassNames
        {
            public const string NearestSampler = "NearestDebugDraw";
            public const string BilinearSampler = "BilinearDebugDraw";
        }

        enum BlitType
        {
            Core = 0, // Core blit
            HDR = 1, // Blit with HDR encoding and overlay UI compositing
            Count = 2
        }

        struct BlitMaterialData
        {
            public Material material;
            public int nearestSamplerPass;
            public int bilinearSamplerPass;
        }

        BlitMaterialData[] m_BlitMaterialData;

        /// <summary>
        /// Creates a new <c>FinalBlitPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="blitMaterial">The <c>Material</c> to use for copying the executing the final blit.</param>
        /// <param name="blitHDRMaterial">The <c>Material</c> to use for copying the executing the final blit when HDR output is active.</param>
        /// <seealso cref="RenderPassEvent"/>
        public FinalBlitPass(RenderPassEvent evt, Material blitMaterial, Material blitHDRMaterial)
        {
            base.profilingSampler = new ProfilingSampler(nameof(FinalBlitPass));
            base.useNativeRenderPass = false;
            m_PassData = new PassData();
            renderPassEvent = evt;

            // Find sampler passes by name
            const int blitTypeCount = (int)BlitType.Count;
            m_BlitMaterialData = new BlitMaterialData[blitTypeCount];
            for (int i = 0; i < blitTypeCount; ++i)
            {
                m_BlitMaterialData[i].material = i == (int)BlitType.Core ? blitMaterial : blitHDRMaterial;
                m_BlitMaterialData[i].nearestSamplerPass = m_BlitMaterialData[i].material?.FindPass(BlitPassNames.NearestSampler) ?? -1;
                m_BlitMaterialData[i].bilinearSamplerPass = m_BlitMaterialData[i].material?.FindPass(BlitPassNames.BilinearSampler) ?? -1;
            }
        }

        /// <summary>
        /// Cleans up resources used by the pass.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Configure the pass
        /// </summary>
        /// <param name="baseDescriptor"></param>
        /// <param name="colorHandle"></param>
        [Obsolete("Use RTHandles for colorHandle")]
        public void Setup(RenderTextureDescriptor baseDescriptor, RenderTargetHandle colorHandle)
        {
            if (m_Source?.nameID != colorHandle.Identifier())
                m_Source = RTHandles.Alloc(colorHandle.Identifier());
        }

        /// <summary>
        /// Configure the pass
        /// </summary>
        /// <param name="baseDescriptor"></param>
        /// <param name="colorHandle"></param>
        public void Setup(RenderTextureDescriptor baseDescriptor, RTHandle colorHandle, bool isLine = true, bool bufferIsLinear = false)
        {
            m_Source = colorHandle;
            m_IsLine = isLine; // *URP14Port-Phase3-B01
            m_BufferIsLinear = bufferIsLinear; // *URP14Port-Phase3-B07FIX-AndroidSrgb
        }

        static void SetupHDROutput(ColorGamut hdrDisplayColorGamut, Material material, HDROutputUtils.Operation hdrOperation, Vector4 hdrOutputParameters)
        {
            material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, hdrOutputParameters);
            HDROutputUtils.ConfigureHDROutput(material, hdrDisplayColorGamut, hdrOperation);
        }

        /// <inheritdoc/>
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref CameraData cameraData = ref renderingData.cameraData;
            DebugHandler debugHandler = GetActiveDebugHandler(ref renderingData);
            bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(ref cameraData);

            if (resolveToDebugScreen)
            {
                ConfigureTarget(debugHandler.DebugScreenColorHandle, debugHandler.DebugScreenDepthHandle);
            }
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool outputsToHDR = renderingData.cameraData.isHDROutputActive;
            InitPassData(ref renderingData, ref m_PassData, outputsToHDR ? BlitType.HDR : BlitType.Core);

            if (m_PassData.blitMaterialData.material == null)
            {
                Debug.LogErrorFormat("Missing {0}. {1} render pass will not execute. Check for missing reference in the renderer resources.", m_PassData.blitMaterialData, GetType().Name);
                return;
            }

            ref CameraData cameraData = ref renderingData.cameraData;

            var cameraTarget = RenderingUtils.GetCameraTargetIdentifier(ref renderingData);
            DebugHandler debugHandler = GetActiveDebugHandler(ref renderingData);
            bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(ref cameraData);

            // Get RTHandle alias to use RTHandle apis
            RTHandleStaticHelpers.SetRTHandleStaticWrapper(cameraTarget);
            var cameraTargetHandle = RTHandleStaticHelpers.s_RTHandleWrapper;

            var cmd = renderingData.commandBuffer;

            // m_Source 是【入队时】捕获的 back；UI 相机 Gamma2Line 在执行期做 back→front 解码并
            // Swap ⇒ 该块此时已是 front（gamma），真正要读的是 swap 后的 back（Linear）。
            // 注意不能用 cameraColorTargetHandle：SwapColorBuffer 走 ConfigureCameraTarget(color, depth)
            // 重载时不刷新该 Handle；且 GetFrontBuffer(cmd) 内部会按 m_Desc 重建 A/B，故用 Peek。
            var urpRenderer = renderingData.cameraData.renderer as UniversalRenderer;
            if (urpRenderer != null && m_Source == urpRenderer.m_ColorBufferSystem.PeekFrontBuffer())
            {
                m_Source = urpRenderer.m_ColorBufferSystem.PeekBackBuffer();
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.FinalBlit)))
            {
                m_PassData.blitMaterialData.material.enabledKeywords = null;

                debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(cmd, ref cameraData, !resolveToDebugScreen);

                // *URP14Port-Phase3-B07FIX：对齐源 FinalBlitPass.cs:61-72 ——
                // isLine=false（缓冲仍是 Gamma，如 UI PP 关时 Gamma2Line 未插）⇒ 先解码一次，
                // 与 backbuffer 写入的编码互逆恒等，原样保留 gamma 结果（源 :69）；
                // isLine=true（缓冲已是 Linear）⇒ 不解码，按平台需要（requireSrgbConversion）编码。
                // 源注释：选错方向就会重复编码（泛白发亮）或重复解码（整体偏暗）。
                // *URP14Port-Phase3-B07FIX-AndroidSrgb：编码补齐第二判据——requireSrgbConversion
                // 依赖 Display.main.requiresSrgbBlitToBackbuffer，在部分安卓设备返回 False 而
                // backbuffer 实际不编码 ⇒ 缓冲为 Linear 时按图形 API 补编码（GLES/Vulkan）。
                // 桌面（D3D11/Metal）两判据均不变，行为与已验证结论完全一致。
                // *URP14Port-Phase3-B07FIX-AndroidSrgb：最终编码统一由 ColorSpacePolicy 判定，
                // 与 Uber（UI PP 开路径）使用同一套语义，避免两条出画路径各算各的。
                // m_BufferIsLinear：出画瞬间缓冲是否为 Linear（gamma 链回程后为 true）。
                bool encodeFinal = ColorSpacePolicy.ShouldEncodeFinal(m_BufferIsLinear,
                    m_PassData.requireSrgbConversion || ColorSpacePolicy.PlatformRequiresManualEncode());
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SRGBToLinearConversion, !m_IsLine);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LinearToSRGBConversion, encodeFinal);

                if (outputsToHDR)
                {
                    VolumeStack stack = VolumeManager.instance.stack;
                    Tonemapping tonemapping = stack.GetComponent<Tonemapping>();

                    Vector4 hdrOutputLuminanceParams;
                    UniversalRenderPipeline.GetHDROutputLuminanceParameters(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, tonemapping, out hdrOutputLuminanceParams);

                    HDROutputUtils.Operation hdrOperation = HDROutputUtils.Operation.None;
                    // If the HDRDebugView is on, we don't want the encoding
                    if (debugHandler == null || !debugHandler.HDRDebugViewIsActive(ref cameraData))
                        hdrOperation |= HDROutputUtils.Operation.ColorEncoding;
                    // Color conversion may have happened in the Uber post process through color grading, so we don't want to reapply it
                    if (!cameraData.postProcessEnabled)
                        hdrOperation |= HDROutputUtils.Operation.ColorConversion;

                    SetupHDROutput(cameraData.hdrDisplayColorGamut, m_PassData.blitMaterialData.material, hdrOperation, hdrOutputLuminanceParams);
                }

                if (resolveToDebugScreen)
                {
                    int shaderPassIndex = m_Source.rt?.filterMode == FilterMode.Bilinear ? m_PassData.blitMaterialData.bilinearSamplerPass : m_PassData.blitMaterialData.nearestSamplerPass;

                    Vector2 viewportScale = m_Source.useScaling ? new Vector2(m_Source.rtHandleProperties.rtHandleScale.x, m_Source.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, m_Source, viewportScale, m_PassData.blitMaterialData.material, shaderPassIndex);

                    cameraData.renderer.ConfigureCameraTarget(debugHandler.DebugScreenColorHandle, debugHandler.DebugScreenDepthHandle);
                }
                else
                {
                    FinalBlitPass.ExecutePass(ref renderingData, m_PassData.blitMaterialData, cameraTargetHandle, m_Source);
                    cameraData.renderer.ConfigureCameraTarget(cameraTargetHandle, cameraTargetHandle);
                }
            }
        }

        private static void ExecutePass(ref RenderingData renderingData, in BlitMaterialData blitMaterialData, RTHandle cameraTarget, RTHandle source)
        {
            var cameraData = renderingData.cameraData;
            var cmd = renderingData.commandBuffer;

            // TODO: Final blit pass should always blit to backbuffer. The first time we do we don't need to Load contents to tile.
            // We need to keep in the pipeline of first render pass to each render target to properly set load/store actions.
            // meanwhile we set to load so split screen case works.
            var loadAction = RenderBufferLoadAction.DontCare;
            if (!cameraData.isSceneViewCamera && !cameraData.isDefaultViewport)
                loadAction = RenderBufferLoadAction.Load;
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
                loadAction = RenderBufferLoadAction.Load;
#endif

            int shaderPassIndex = source.rt?.filterMode == FilterMode.Bilinear ? blitMaterialData.bilinearSamplerPass : blitMaterialData.nearestSamplerPass;
            RenderingUtils.FinalBlit(cmd, ref cameraData, source, cameraTarget, loadAction, RenderBufferStoreAction.Store, blitMaterialData.material, shaderPassIndex);
        }

        private class PassData
        {
            internal TextureHandle source;
            internal TextureHandle destination;

            internal int sourceID;
            internal Vector4 hdrOutputLuminanceParams;
            internal bool requireSrgbConversion;
            internal BlitMaterialData blitMaterialData;
            internal RenderingData renderingData;
        }

        /// <summary>
        /// Initialize the shared pass data.
        /// </summary>
        /// <param name="passData"></param>
        private void InitPassData(ref RenderingData renderingData, ref PassData passData, BlitType blitType)
        {
            passData.renderingData = renderingData;
            passData.requireSrgbConversion = renderingData.cameraData.requireSrgbConversion;

            passData.blitMaterialData = m_BlitMaterialData[(int)blitType];
        }

        internal void Render(RenderGraph renderGraph, ref RenderingData renderingData, TextureHandle src, TextureHandle dest, TextureHandle overlayUITexture)
        {
            using (var builder = renderGraph.AddRenderPass<PassData>("Final Blit", out var passData, base.profilingSampler))
            {
                bool outputsToHDR = renderingData.cameraData.isHDROutputActive;
                passData.source = src;
                passData.destination = dest;
                passData.sourceID = ShaderPropertyId.sourceTex;

                InitPassData(ref renderingData, ref passData, outputsToHDR ? BlitType.HDR : BlitType.Core);

                builder.UseColorBuffer(passData.destination, 0);
                builder.ReadTexture(passData.source);

                if (outputsToHDR && overlayUITexture.IsValid())
                {
                    VolumeStack stack = VolumeManager.instance.stack;
                    Tonemapping tonemapping = stack.GetComponent<Tonemapping>();
                    ref CameraData cameraData = ref renderingData.cameraData;
                    UniversalRenderPipeline.GetHDROutputLuminanceParameters(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, tonemapping, out passData.hdrOutputLuminanceParams);

                    builder.ReadTexture(overlayUITexture);
                }
                else
                {
                    passData.hdrOutputLuminanceParams = new Vector4(-1.0f, -1.0f, -1.0f, -1.0f);
                }

                builder.SetRenderFunc((PassData data, RenderGraphContext context) =>
                {
                    data.blitMaterialData.material.enabledKeywords = null;

                    CoreUtils.SetKeyword(context.cmd, ShaderKeywordStrings.LinearToSRGBConversion, data.requireSrgbConversion);
                    data.blitMaterialData.material.SetTexture(data.sourceID, data.source);

                    DebugHandler debugHandler = GetActiveDebugHandler(ref data.renderingData);
                    bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(ref data.renderingData.cameraData);

                    // TODO RENDERGRAPH: this should ideally be shared in ExecutePass to avoid code duplication
                    if (data.hdrOutputLuminanceParams.w >= 0)
                    {
                        HDROutputUtils.Operation hdrOperation = HDROutputUtils.Operation.None;
                        // If the HDRDebugView is on, we don't want the encoding
                        if (debugHandler == null || !debugHandler.HDRDebugViewIsActive(ref data.renderingData.cameraData))
                            hdrOperation |= HDROutputUtils.Operation.ColorEncoding;

                        // Color conversion may have happened in the Uber post process through color grading, so we don't want to reapply it
                        if (!data.renderingData.cameraData.postProcessEnabled)
                            hdrOperation |= HDROutputUtils.Operation.ColorConversion;

                        SetupHDROutput(data.renderingData.cameraData.hdrDisplayColorGamut, data.blitMaterialData.material, hdrOperation, data.hdrOutputLuminanceParams);
                    }

                    if (resolveToDebugScreen)
                    {
                        RTHandle sourceTex = data.source;
                        Vector2 viewportScale = sourceTex.useScaling ? new Vector2(sourceTex.rtHandleProperties.rtHandleScale.x, sourceTex.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                        int shaderPassIndex = sourceTex.rt?.filterMode == FilterMode.Bilinear ? data.blitMaterialData.bilinearSamplerPass : data.blitMaterialData.nearestSamplerPass;
                        Blitter.BlitTexture(context.cmd, sourceTex, viewportScale, data.blitMaterialData.material, shaderPassIndex);
                    }
                    else
                        ExecutePass(ref data.renderingData, data.blitMaterialData, data.destination, data.source);
                });
            }
        }
    }
}
