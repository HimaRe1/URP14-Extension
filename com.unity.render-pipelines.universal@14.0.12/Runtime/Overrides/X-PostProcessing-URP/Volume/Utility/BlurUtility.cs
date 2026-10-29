using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：XPP Blur 家族的 URP14 RTHandle 适配工具。
    ///
    /// 【为什么需要本工具 —— 源项目（URP12）的做法】
    ///   源项目用 cmd.GetTemporaryRT(int nameID, w, h, 0, FilterMode.Bilinear) 申请临时 RT，
    ///   在帧内用 cmd.ReleaseTemporaryRT(nameID) 释放。该"按 nameID 索引的临时 RT 池"
    ///   与 URP14 的 RTHandle 体系互不相通（两者不是同一套资源池），且 GetTemporaryRT 在
    ///   URP14 已不推荐（无法参与 RTHandle 的尺寸/引用计数管理）。
    ///   因此本批改为：每个 Blur Renderer **持有自己的 RTHandle**，
    ///   由 BlurUtility.EnsureBuffer 按需分配 / 按尺寸变化重分配，在 Renderer.Cleanup() 中释放。
    ///
    /// 【为什么不用 RenderingUtils.ReAllocateIfNeeded —— 池别名风险】
    ///   RenderingUtils.ReAllocateIfNeeded 走 URP 全局 RTHandleResourcePool，池的 key 是
    ///   TextureDesc（尺寸/格式/filter/wrap 等）。两个 Blur 效果若请求**完全相同**的描述符，
    ///   会命中同一条池记录而**共享同一个 RenderTexture** —— 效果 A 的中间结果会被效果 B 覆盖。
    ///   本批改为每个 Renderer 独立 RTHandles.Alloc，彻底规避跨效果别名。
    ///
    /// 【分辨率基准 —— 为什么不用 Screen.width】
    ///   源项目用 Screen.width/height 作为基准。D1-a 的 m_RenderScale=0.25 使效果链实际
    ///   工作在**缩放后的内部缓冲**上（640×360 目标 → 160×90 链缓冲），此时 Screen.width
    ///   既不是链缓冲尺寸、也不可靠（batchmode 下为 Game View 尺寸）。
    ///   这与 Batch 04-C 把 shader 里的 _ScreenParams 换成 _ScaledScreenParams 是同一类问题：
    ///   **凡是"以被采样缓冲的 texel 为单位"的量，都必须按链工作分辨率换算**。
    ///   故本批统一改用 PostProcessPass 注入的 cameraTargetDescriptor（= 链工作缓冲尺寸）。
    ///
    /// 【颜色格式】
    ///   源项目的 GetTemporaryRT 使用默认格式（LDR ARGB32），在 HDR 链路上会截断高光。
    ///   本批沿用链工作缓冲自身的 graphicsFormat（与 PostProcessPass.GetCompatibleDescriptor
    ///   的做法一致），避免中间缓冲引入额外的色域/精度损失。
    /// </summary>
    public static class BlurUtility
    {
        /// <summary>
        /// 链工作缓冲的像素尺寸。未注入描述符时（理论上不会发生）回退到 Screen 尺寸，
        /// 以保证 Renderer 在脱离 PostProcessPass 的上下文中也不会除零。
        /// </summary>
        public static Vector2Int GetChainSize(RenderTextureDescriptor chainDescriptor)
        {
            if (chainDescriptor.width > 0 && chainDescriptor.height > 0)
                return new Vector2Int(chainDescriptor.width, chainDescriptor.height);

            return new Vector2Int(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
        }

        /// <summary>
        /// 由链工作缓冲描述符派生 Blur 中间缓冲的描述符。
        /// 固定为单采样（msaa=1）、无深度、无 mipmap —— 对应源项目 GetTemporaryRT 的 depthBuffer=0。
        /// </summary>
        public static RenderTextureDescriptor MakeBufferDescriptor(in RenderTextureDescriptor chainDescriptor, int width, int height)
        {
            RenderTextureDescriptor desc = chainDescriptor;
            desc.width = Mathf.Max(1, width);
            desc.height = Mathf.Max(1, height);
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.enableRandomWrite = false;
            desc.bindMS = false;
            desc.useDynamicScale = false;
            desc.memoryless = RenderTextureMemoryless.None;
            desc.vrUsage = VRTextureUsage.None;
            return desc;
        }

        /// <summary>
        /// 按需分配 / 重分配一个**固定尺寸**的中间缓冲 RTHandle。
        /// 尺寸或格式变化时释放旧句柄并重新分配（源项目是每帧 Get/Release，
        /// RTHandle 方案下跨帧复用同一句柄以避免每帧的分配抖动）。
        ///
        /// 固定尺寸（而非 RTHandle 的自动缩放）是为了保留源算法的**逐级 Mip 金字塔尺寸关系**：
        /// Dual 系列每一层的缓冲尺寸是上一层的 1/2，自动缩放 RTHandle 会破坏这个比例关系。
        /// </summary>
        public static void EnsureBuffer(ref RTHandle handle, in RenderTextureDescriptor descriptor, string name)
        {
            if (handle != null && handle.rt != null &&
                handle.rt.width == descriptor.width &&
                handle.rt.height == descriptor.height &&
                handle.rt.graphicsFormat == descriptor.graphicsFormat)
            {
                return;
            }

            ReleaseBuffer(ref handle);
            handle = RTHandles.Alloc(descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: name);
        }

        /// <summary>释放中间缓冲（句柄置空，允许重复调用）。</summary>
        public static void ReleaseBuffer(ref RTHandle handle)
        {
            if (handle == null)
                return;

            handle.Release();
            handle = null;
        }

        /// <summary>
        /// 纯拷贝步骤（源项目里的 cmd.Blit(source, someTempRT) —— 无材质的一次纹理拷贝）。
        ///
        /// 源项目的拷贝会同时完成**降采样**（目标 RT 比源小，RTDownScaling &gt; 1 时），
        /// 采样行为等价于用源纹理的 sampler 状态做一次重采样。
        /// 本批保持同一条 legacy cmd.Blit 通路（URP14 自身的 RenderingUtils 亦使用该调用），
        /// 以保证与源项目的采样/过滤行为一致；中间缓冲的 filterMode 为 Bilinear，
        /// 与源项目 GetTemporaryRT(..., FilterMode.Bilinear) 相同。
        /// </summary>
        public static void CopyTexture(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination)
        {
            cmd.Blit(source, destination);
        }
    }
}
