using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using System.Linq;

// *URP14Port-Phase3-B04A
// 说明：本文件自 URP12 源项目迁移。原构造函数注册约 70 个效果渲染器，
// 这些效果尚未迁移到 URP14；为避免编译失败，除 Batch 04-A 的最小效果
// （ColorAdjustmentSaturationRenderer）外，其余注册项全部保留为注释，
// 待对应效果批次迁移后逐行解禁。其余逻辑与 URP12 源逐字一致。
namespace XPostProcessing
{
    public class CustomPostProcess
    {

        const string k_RenderPostProcessingTag = "Custom Render PostProcessing Effects";
       // const string k_RenderFinalPostProcessingTag = "Custom Render Final PostProcessing Pass";
        private static readonly ProfilingSampler m_ProfilingRenderPostProcessing = new ProfilingSampler(k_RenderPostProcessingTag);
       // private static readonly ProfilingSampler m_ProfilingRenderFinalPostProcessing = new ProfilingSampler(k_RenderFinalPostProcessingTag);

        List<AbstractVolumeRenderer> m_PostProcessingRenderers = new List<AbstractVolumeRenderer>();
        List<AbstractVolumeRenderer> m_AcitvePostProcessing = null;

        //RenderTargetIdentifier m_Source;
        //RenderTargetHandle m_TempRT0;
        //RenderTargetHandle m_TempRT1;
        //RenderTextureDescriptor m_Descriptor;


       // public int downSample;

        public CustomPostProcess()
        {
           // m_TempRT0.Init("_TempRT0");
           // m_TempRT1.Init("_TempRT1");

            // var customVolumes = VolumeManager.instance.baseComponentTypeArray
            //     .Where(t => t.IsSubclassOf(typeof(VolumeSetting)))
            //     .Select(t => VolumeManager.instance.stack.GetComponent(t) as VolumeSetting)
            //     .ToList();
            // Debug.LogError("customVolumesL:" + customVolumes.Count);

            // for (int i = 0; i < customVolumes.Count; i++)
            // {
            //     AddEffect(customVolumes[i]);
            // }

            AddEffect(new DepthFogRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁（深度依赖已验证，见 BATCH-04E-REPORT）
            AddEffect(new CloudShadowRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁
            //故障
            AddEffect(new GlitchRGBSplitRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new GlitchRGBSplitV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchRGBSplitV3Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchRGBSplitV4Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchRGBSplitV5Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchDigitalStripeRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchImageBlockRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchImageBlockV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchImageBlockV3Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchImageBlockV4Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchLineBlockRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchTileJitterRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchScanLineJitterRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchAnalogNoiseRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchScreenJumpRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchScreenShakeRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new GlitchWaveJitterRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            //边缘检测
            AddEffect(new EdgeDetectionRobertsRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionRobertsNeonRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionRobertsNeonV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionScharrRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionScharrNeonRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionScharrNeonV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionSobelRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionSobelNeonRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new EdgeDetectionSobelNeonV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            //像素化
            AddEffect(new PixelizeCircleRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new PixelizeDiamondRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeHexagonRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeHexagonGridRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeLeafRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeLedRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeQuadRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new PixelizeSectorRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new PixelizeTriangleRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁


            // *URP14Port-Phase3-B04D：此处对应源项目注册表第 88 行（IrisBlur 首次注册，位于 Blur 段之前）。
            // 源项目在 Blur 段内（第 105 行）**重复注册了一次** IrisBlurRenderer。
            // 本批去重：只解禁这一处（保持源项目的效果执行顺序），Blur 段内的重复行保持注释并留追溯说明。
            AddEffect(new IrisBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁（源项目第 88 行；重复注册已去重）
            AddEffect(new RainRippleRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁

            AddEffect(new SurfaceSnowRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁
            AddEffect(new RaderWaveRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁
            AddEffect(new BulletTimeRenderer()); // *URP14Port-Phase3-B04E 已迁移并解禁

            //Blur
            AddEffect(new GaussianBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new BoxBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new KawaseBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new DualBoxBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new DualGaussianBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new DualKawaseBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new DualTentBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new BokehBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new TiltShiftBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            // AddEffect(new IrisBlurRenderer()); // *URP14Port-Phase3-B04D **源项目在此处（源注册表第 105 行）重复注册了一次**
            //   IrisBlurRenderer —— 源项目注册表第 88 行已注册过同一效果。
            //   为避免同一效果在效果链里被执行两次（双倍模糊），本批**只解禁源项目第 88 行对应的那一处**
            //   （见本文件上方第 93 行），此处保留原始注释行与追溯说明。
            //   若需完全复刻源项目的"执行两次"行为，解禁本行即可（届时将与上方第 93 行构成重复注册）。
            AddEffect(new IrisBlurV2Renderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new GrainyBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new RadialBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new RadialBlurV2Renderer()); // *URP14Port-Phase3-B04D 已迁移并解禁
            AddEffect(new DirectionalBlurRenderer()); // *URP14Port-Phase3-B04D 已迁移并解禁

            //Image Processing
            AddEffect(new SharpenV1Renderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new SharpenV2Renderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new SharpenV3Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁


            //Vignette
            AddEffect(new RapidOldTVVignetteRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new RapidOldTVVignetteV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new RapidVignetteRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new RapidVignetteV2Renderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new AuroraVignetteRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁

            //色彩调整
            AddEffect(new ColorAdjustmentBleachBypassRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentBrightnessRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new ColorAdjustmentContrastRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new ColorAdjustmentContrastV2Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentContrastV3Renderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentHueRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new ColorAdjustmentLensFilterRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentSaturationRenderer()); // *URP14Port-Phase3-B04A 最小可运行效果（Batch 04-A 端到端验证）
            AddEffect(new ColorAdjustmentTechnicolorRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentTintRenderer()); // *URP14Port-Phase3-B04B 已迁移并解禁
            AddEffect(new ColorAdjustmentWhiteBalanceRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ColorAdjustmentColorReplaceRenderer()); // *URP14Port-Phase3-B04C 已迁移并解禁
            AddEffect(new ScreenBinarizationRenderer()); // *URP14Port-Phase3-B04F 已迁移并解禁（效果库 77/77 闭合）

            m_AcitvePostProcessing = new List<AbstractVolumeRenderer>(m_PostProcessingRenderers.Count);
        }

        private void AddEffect(AbstractVolumeRenderer renderer)
        {
            m_PostProcessingRenderers.Add(renderer);
            renderer.Init();
        }

        public void Cleanup()
        {
            for (int i = 0; i < m_PostProcessingRenderers.Count; i++)
            {
                m_PostProcessingRenderers[i].Cleanup();
            }
            m_PostProcessingRenderers = new List<AbstractVolumeRenderer>();
            m_AcitvePostProcessing = null;
        }

        //public void Setup(RenderTargetIdentifier source)
        //{
        //    m_Source = source;
        //    // m_Destination = destination;
        //}
        //
        //public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        //{
        //    RenderTextureDescriptor cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
        //    m_Descriptor = cameraTargetDescriptor;
        //    m_Descriptor.msaaSamples = 1;
        //    m_Descriptor.depthBufferBits = 32;
        //    m_Descriptor.width = m_Descriptor.width >> downSample;
        //    m_Descriptor.height = m_Descriptor.height >> downSample;
        //
        //
        //    cmd.GetTemporaryRT(m_TempRT0.id, m_Descriptor);
        //    cmd.GetTemporaryRT(m_TempRT1.id, m_Descriptor);
        //
        //    ConfigureTarget(m_TempRT0.Identifier());
        //    ConfigureClear(ClearFlag.None, Color.white);
        //}
        //
        //public override void OnCameraCleanup(CommandBuffer cmd)
        //{
        //
        //    cmd.ReleaseTemporaryRT(m_TempRT0.id);
        //    cmd.ReleaseTemporaryRT(m_TempRT1.id);
        //}

        public List<AbstractVolumeRenderer> GetAcitvePostProcessingRenderers()
        {
            m_AcitvePostProcessing.Clear();
            foreach (var renderer in m_PostProcessingRenderers)
            {
                if (renderer.IsActive())
                {
                    m_AcitvePostProcessing.Add(renderer);
                }
            }
            return m_AcitvePostProcessing;
        }

        //public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        //{
        //    // if (!Application.isPlaying) return;
        //
        //    var cmd = CommandBufferPool.Get();
        //    cmd.Clear();
        //
        //   //// 初始化临时RT
        //   //RenderTargetIdentifier buff0, buff1;
        //   //buff0 = m_TempRT0.id;
        //   //buff1 = m_TempRT1.id;
        //   //RenderTargetIdentifier GetSource() => buff0;
        //   //RenderTargetIdentifier GetTarget() => buff1;
        //
        //    void Swap() => CoreUtils.Swap(ref buff0, ref buff1);
        //    using (new ProfilingScope(cmd, m_ProfilingRenderPostProcessing))
        //    {
        //        Blit(cmd, m_Source, buff0);
        //        foreach (var renderer in m_PostProcessingRenderers)
        //        {
        //            if (renderer.IsActive())
        //            {
        //                renderer.Render(cmd, GetSource(), GetTarget());
        //                Swap();
        //            }
        //        }
        //
        //        Blit(cmd, buff0, m_Source);
        //    }
        //
        //
        //    context.ExecuteCommandBuffer(cmd);
        //    CommandBufferPool.Release(cmd);
        //}

    }
}