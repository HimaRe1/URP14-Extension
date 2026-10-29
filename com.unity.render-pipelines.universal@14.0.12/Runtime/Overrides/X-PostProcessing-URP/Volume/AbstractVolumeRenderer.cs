using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace XPostProcessing
{
    public abstract class AbstractVolumeRenderer
    {
        public abstract bool IsActive();
        public abstract bool Init();
        public abstract void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target);
        public virtual void Cleanup() { }

        // *URP14Port-Phase3-B04D：链工作缓冲描述符注入点。
        //
        // 【为什么需要】
        //   源项目（URP12）的 Blur 家族用 Screen.width/height 作为尺寸与偏移基准。
        //   D1-a 的 m_RenderScale = 0.25 使效果链实际工作在**缩放后的内部缓冲**上
        //   （640×360 目标 → 160×90 链缓冲），此时 Screen.width 既不是链缓冲尺寸、
        //   也不可靠（batchmode 下是 Game View 尺寸）。
        //   这与 Batch 04-C 把 shader 里的 _ScreenParams 换成 _ScaledScreenParams 是同一类问题：
        //   **凡是"以被采样缓冲的 texel 为单位"的量，都必须按链工作分辨率换算**。
        //
        // 【值的来源】
        //   PostProcessPass.Execute 在渲染每个 XPP 效果前注入 m_Descriptor，
        //   该值 = cameraTargetDescriptor（= cameraData.scaledWidth/Height，已含 renderScale 缩放），
        //   即**链工作缓冲的真实尺寸**。
        private RenderTextureDescriptor m_ChainDescriptor;

        /// <summary>链工作缓冲描述符（已含 renderScale 缩放）。未注入时为默认值（width/height = 0）。</summary>
        public RenderTextureDescriptor ChainDescriptor => m_ChainDescriptor;

        /// <summary>由 PostProcessPass 注入链工作缓冲描述符。</summary>
        public void SetChainDescriptor(RenderTextureDescriptor descriptor) => m_ChainDescriptor = descriptor;

        // *URP14Port-Phase3-B04D：相机**输出**分辨率注入点。
        //
        // 【为什么需要两个分辨率基准】
        //   Blur 家族的 shader 里存在两类完全不同的"分辨率相关量"，必须分别用不同的基准：
        //
        //   ① **纯 UV 偏移量**（GaussianBlur/BoxBlur/DualGaussianBlur/DualBoxBlur/DualTentBlur 的
        //      `_BlurOffset`，BokehBlur/IrisBlurV2 的 `_Params.zw`，GrainyBlur 的 `_BlurRadius` …）：
        //      这些值在 shader 里**直接当作 UV 偏移**使用，不乘以 texel 尺寸。
        //      源项目用 `Screen.width/height` 作分母 → 语义是"相对于**输出画面**的偏移比例"。
        //      必须沿用输出分辨率作分母，否则在 D1-a（renderScale=0.25）下模糊强度会整体放大 4 倍。
        //
        //   ② **texel 步长 / 采样缓冲像素数**（如 KawaseBlur 的 `_Offset`，它与 `_MainTex_TexelSize` 相乘）：
        //      语义是"被采样缓冲的 texel 数"，必须按**链工作缓冲**分辨率换算
        //      —— 这与 Batch 04-C 把 `_ScreenParams` 换成 `_ScaledScreenParams` 是同一类问题。
        //
        //   ⇒ 中间缓冲的**尺寸**用链工作分辨率；UV 偏移的**分母**用输出分辨率。
        private Vector2Int m_OutputSize = Vector2Int.zero;

        /// <summary>相机输出分辨率（像素）。源项目此处使用 Screen.width/height。</summary>
        public Vector2Int OutputSize => m_OutputSize;

        /// <summary>由 PostProcessPass 注入相机输出分辨率。</summary>
        public void SetOutputSize(Vector2Int size) => m_OutputSize = size;
    }

    public abstract class VolumeRenderer<T> : AbstractVolumeRenderer where T : VolumeSetting
    {
        public T settings => VolumeManager.instance.stack.GetComponent<T>();
        public override bool IsActive()
        {
            bool active=  settings.IsActive();
            if (!active) return false;
            return m_BlitMaterial != null;
        } 

        private Shader m_Shader = null;
        public  Material m_BlitMaterial=null;
        public override bool Init()
        {
            string shaderName = settings.GetShaderName();
            m_Shader = null ;
            if (!string.IsNullOrEmpty(shaderName))
            {
                m_Shader = Shader.Find(shaderName);
            }
            if (m_Shader != null)
            {
                m_BlitMaterial = CoreUtils.CreateEngineMaterial(m_Shader);
                return true;
            }
            return false;
        }

        public override void Cleanup()
        {
            if (m_BlitMaterial != null)
            {
                CoreUtils.Destroy(m_BlitMaterial);
                m_BlitMaterial = null;
            }
        }
        // public override void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target)
        // {

        //     if (settings.IsActive())
        //         return;

        //     Render(cmd, source, target);
        // }

        // public abstract void Render(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier target);

    }

}