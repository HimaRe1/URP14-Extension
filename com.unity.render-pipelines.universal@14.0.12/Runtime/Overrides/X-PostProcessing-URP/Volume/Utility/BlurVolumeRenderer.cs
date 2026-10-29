using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPostProcessing
{
    /// <summary>
    /// *URP14Port-Phase3-B04D：XPP Blur 家族的共享渲染器基类。
    ///
    /// 【职责】
    ///   1. 持有本效果自己的中间缓冲 RTHandle（每个效果独立，避免 URP 全局池的跨效果别名）。
    ///   2. 按需分配 / 按尺寸变化重分配（尺寸或格式变化时才重新分配，帧内零抖动）。
    ///   3. 在 Cleanup() 中释放全部中间缓冲。
    ///
    /// 【为什么需要"按 pass 分配材质实例"】
    ///   Blur 家族的参数 uniform（`_BlurOffset` / `_Params` / `_Offset` / `_Gradient` / `_GoldenRot`）
    ///   **只声明在 shader 的 HLSLINCLUDE 块里，不在 Properties 块中** ⇒ 它们是**材质常量**
    ///   （不是全局变量）：`Material.SetVector/SetFloat` 生效，
    ///   `cmd.SetGlobalVector/SetGlobalFloat` 会被材质常量覆盖（实测两条通路结果一致）。
    ///
    ///   材质常量在**命令缓冲执行期**才上传到 GPU ⇒ 同一 CommandBuffer 内用同一个材质做多次 Blit 时，
    ///   **所有 Blit 都会读到最后一次写入的值**（末值覆盖）。
    ///   这在 URP12 源项目里同样成立（源项目用 `m_BlitMaterial.SetVector`），
    ///   源项目的水平/垂直两趟模糊因而实际共用同一个偏移。
    ///
    ///   本批的处理：**每个需要不同参数值的 pass 使用独立的材质实例**（slot 机制），
    ///   使每个 slot 的常量互不干扰，恢复"水平 pass 用水平偏移、垂直 pass 用垂直偏移"的算法语义。
    ///
    /// 【FIX-09 —— 另一个独立且更关键的坑：三参数 cmd.Blit 在多 Pass shader 上丢失材质常量】
    ///   实测（URP14 / 2022.3.62f2，判别实验见 BATCH-04D-REPORT）：
    ///     · 单 Pass shader：`cmd.Blit(src, dst, mat)` 与 `cmd.Blit(src, dst, mat, 0)` **都正常**；
    ///     · **多 Pass** shader：三参数形式**完全不写材质常量**（uniform 恒为 0 ⇒ 效果彻底失效），
    ///       四参数形式（显式 pass index）**100% 正常**。
    ///   Blur 家族里 GaussianBlur / BoxBlur / Dual* / BokehBlur 等都是双 Pass
    ///   （Pass 0 = 模糊，Pass 1 = FragCombine 直通）⇒ 三参数形式下这些效果**全部静默失效**。
    ///   本批统一把 Blur 家族的 Blit 改为**四参数形式**（显式 pass index，语义与源项目一致）。
    ///   这也解释了 Batch 04-C 的 55 个效果（多为单 Pass）为何全部通过验证。
    /// 【生命周期】
    ///   PostProcessPasses.Recreate() → PostProcessPass.Cleanup() → CustomPostProcess.Cleanup()
    ///   → 每个 Renderer 的 Cleanup()。因此中间缓冲的释放点与材质的销毁点一致，
    ///   而 PostProcessPass 被重建时 CustomPostProcess 也会重建 → 下一帧重新分配。
    /// </summary>
    public abstract class BlurVolumeRenderer<T> : VolumeRenderer<T> where T : VolumeSetting
    {
        /// <summary>中间缓冲句柄（按效果自行编号）。</summary>
        protected RTHandle[] m_Buffers = Array.Empty<RTHandle>();

        /// <summary>按 pass 分配的材质实例（slot 0 = 主材质 m_BlitMaterial，slot ≥1 = 额外材质）。</summary>
        private Material[] m_SlotMaterials = Array.Empty<Material>();

        /// <summary>链工作缓冲尺寸（像素）—— 替代源项目的 Screen.width/height。</summary>
        protected Vector2Int ChainSize => BlurUtility.GetChainSize(ChainDescriptor);

        /// <summary>
        /// 相机**输出**分辨率（像素）—— 对应源项目的 Screen.width/height。
        /// 用于**纯 UV 偏移**的分母（中间缓冲的尺寸用 ChainSize，两者是不同的基准）。
        /// </summary>
        protected Vector2Int ScreenSize =>
            (OutputSize.x > 0 && OutputSize.y > 0) ? OutputSize : ChainSize;

        /// <summary>
        /// 取得指定 slot 的材质实例。
        /// slot 0 返回主材质 <see cref="VolumeRenderer{T}.m_BlitMaterial"/>；
        /// slot ≥1 懒创建并缓存独立材质实例。
        /// </summary>
        protected Material GetSlotMaterial(int slot)
        {
            if (slot <= 0)
                return m_BlitMaterial;

            if (slot >= m_SlotMaterials.Length)
            {
                Material[] grown = new Material[slot + 1];
                Array.Copy(m_SlotMaterials, grown, m_SlotMaterials.Length);
                m_SlotMaterials = grown;
            }

            if (m_SlotMaterials[slot] == null)
            {
                Shader shader = m_BlitMaterial != null ? m_BlitMaterial.shader : null;
                if (shader == null)
                    return null;

                m_SlotMaterials[slot] = CoreUtils.CreateEngineMaterial(shader);
            }

            return m_SlotMaterials[slot];
        }

        /// <summary>按需分配 / 重分配第 index 个中间缓冲，返回句柄。</summary>
        protected RTHandle EnsureBuffer(int index, int width, int height, string name)
        {
            if (index < 0)
                return null;

            if (index >= m_Buffers.Length)
            {
                int newSize = Mathf.Max(index + 1, m_Buffers.Length * 2);
                RTHandle[] grown = new RTHandle[newSize];
                Array.Copy(m_Buffers, grown, m_Buffers.Length);
                m_Buffers = grown;
            }

            RenderTextureDescriptor desc = BlurUtility.MakeBufferDescriptor(ChainDescriptor, width, height);
            BlurUtility.EnsureBuffer(ref m_Buffers[index], desc, name);
            return m_Buffers[index];
        }

        /// <summary>释放全部中间缓冲（可重复调用）。</summary>
        protected void ReleaseBuffers()
        {
            for (int i = 0; i < m_Buffers.Length; i++)
                BlurUtility.ReleaseBuffer(ref m_Buffers[i]);
        }

        public override void Cleanup()
        {
            base.Cleanup();
            ReleaseBuffers();

            for (int i = 0; i < m_SlotMaterials.Length; i++)
            {
                if (m_SlotMaterials[i] != null)
                {
                    CoreUtils.Destroy(m_SlotMaterials[i]);
                    m_SlotMaterials[i] = null;
                }
            }
        }
    }
}
