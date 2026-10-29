using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace UnityEngine.Rendering.Universal.Internal
{
    internal sealed class RenderTargetBufferSystem
    {
        struct SwapBuffer
        {
            public RTHandle rtMSAA;
            public RTHandle rtResolve;
            public string name;
            public int msaa;
        }
        SwapBuffer m_A, m_B;
        static bool m_AisBackBuffer = true;

        static RenderTextureDescriptor m_Desc;
        FilterMode m_FilterMode;
        bool m_AllowMSAA = true;

        ref SwapBuffer backBuffer { get { return ref m_AisBackBuffer ? ref m_A : ref m_B; } }
        ref SwapBuffer frontBuffer { get { return ref m_AisBackBuffer ? ref m_B : ref m_A; } }

        public RenderTargetBufferSystem(string name)
        {
            m_A.name = name + "A";
            m_B.name = name + "B";
        }

        public void Dispose()
        {
            m_A.rtMSAA?.Release();
            m_B.rtMSAA?.Release();
            m_A.rtResolve?.Release();
            m_B.rtResolve?.Release();
        }

        public RTHandle PeekBackBuffer()
        {
            return (m_AllowMSAA && backBuffer.msaa > 1) ? backBuffer.rtMSAA : backBuffer.rtResolve;
        }

        // *URP14Port-D1a 新增：不触发 ReAllocate 的 front buffer 只读访问。
        // URP14 的 GetFrontBuffer(cmd) 内部会 ReAllocate()，把 A/B 一起按 m_Desc 重建，
        // 这会立刻把刚 resize 过的 front 打回旧尺寸。分步 resize 流程必须绕开它。
        //
        // 与官方 GetFrontBuffer(cmd) 保持一致：MSAA 被关闭时把 frontBuffer.msaa 归一为 1，
        // 否则会返回 rtMSAA 而不是本该使用的 rtResolve。
        public RTHandle PeekFrontBuffer()
        {
            if (!m_AllowMSAA && frontBuffer.msaa > 1)
                frontBuffer.msaa = 1;

            return (m_AllowMSAA && frontBuffer.msaa > 1) ? frontBuffer.rtMSAA : frontBuffer.rtResolve;
        }

        // *URP14Port-D1a 暴露 m_Desc 供 BlitPass 判定 needChangeSize（对齐 URP12 魔改里 m_Desc 为 internal static）。
        public static RenderTextureDescriptor Desc => m_Desc;

        // *URP14Port-D1a：URP12 魔改 ReSizeFrontBuffer 的 URP14 等价实现。
        // 只重建 front buffer 到目标尺寸；**不改动 m_Desc**，也不重建 back buffer，
        // 因此 back 里的场景内容此刻仍然完好（这是"分步 resize"能保住画面的关键）。
        //
        // 传入 targetDesc 而不是只传宽高：颜色缓冲必须与 UI 相机的最终输出 descriptor
        // 共用同一来源，才能保证 graphicsFormat / msaaSamples 与深度、resolve 资源一致。
        public void ReSizeFrontBuffer(CommandBuffer cmd, RenderTextureDescriptor targetDesc)
        {
            ReSizeOne(cmd, ref frontBuffer, targetDesc);
        }

        // *URP14Port-D1a：URP12 魔改 ReSizeBackBufferAndSave 的 URP14 等价实现。
        // 重建 back buffer 到目标尺寸，并把新尺寸写回 m_Desc（"AndSave"= 持久化 descriptor），
        // 使后续 GetBackBuffer(cmd) 的 ReAllocate 尺寸匹配、不再重建。
        // 调用前必须已把内容 blit 到 front，否则 back 内容会丢失。
        //
        // ⚠️ 生命周期：ReAllocateIfNeeded 会立即把旧 RTHandle 放入 stale pool，池满则直接 Release。
        // 因此调用方必须保证 back 作为 blit source 的命令已经 ExecuteCommandBuffer 提交过。
        public void ReSizeBackBufferAndSave(CommandBuffer cmd, RenderTextureDescriptor targetDesc)
        {
            m_Desc.width = targetDesc.width;
            m_Desc.height = targetDesc.height;
            ReSizeOne(cmd, ref backBuffer, targetDesc);
        }

        void ReSizeOne(CommandBuffer cmd, ref SwapBuffer buf, RenderTextureDescriptor targetDesc)
        {
            // 以目标 descriptor（UI 输出 descriptor）为基准，只覆盖本 BufferSystem 需要的字段，
            // 其余（graphicsFormat / sRGB / dynamicScale / dimension …）与目标保持一致。
            var desc = targetDesc;
            desc.depthBufferBits = 0;
            desc.mipCount = 0;

            desc.msaaSamples = buf.msaa;
            if (desc.msaaSamples <= 1)
                desc.bindMS = false;

            if (m_AllowMSAA && buf.msaa > 1)
                RenderingUtils.ReAllocateIfNeeded(ref buf.rtMSAA, desc, m_FilterMode, TextureWrapMode.Clamp, name: buf.name);

            desc.msaaSamples = 1;
            desc.bindMS = false;
            RenderingUtils.ReAllocateIfNeeded(ref buf.rtResolve, desc, m_FilterMode, TextureWrapMode.Clamp, name: buf.name);

            // ReAllocate 里 ReAllocateIfNeeded 会重绑全局纹理名；这里补一次确保 shader 采样到新 RT。
            cmd.SetGlobalTexture(buf.name, buf.rtResolve);
        }

        public RTHandle GetBackBuffer(CommandBuffer cmd)
        {
            ReAllocate(cmd);
            return PeekBackBuffer();
        }

        public RTHandle GetFrontBuffer(CommandBuffer cmd)
        {
            if (!m_AllowMSAA && frontBuffer.msaa > 1)
                frontBuffer.msaa = 1;

            ReAllocate(cmd);

            return (m_AllowMSAA && frontBuffer.msaa > 1) ? frontBuffer.rtMSAA : frontBuffer.rtResolve;
        }

        public void Swap()
        {
            m_AisBackBuffer = !m_AisBackBuffer;
        }

        void ReAllocate(CommandBuffer cmd)
        {
            var desc = m_Desc;

            desc.msaaSamples = m_A.msaa;
            if (desc.msaaSamples > 1)
                RenderingUtils.ReAllocateIfNeeded(ref m_A.rtMSAA, desc, m_FilterMode, TextureWrapMode.Clamp, name: m_A.name);

            desc.msaaSamples = m_B.msaa;
            if (desc.msaaSamples > 1)
                RenderingUtils.ReAllocateIfNeeded(ref m_B.rtMSAA, desc, m_FilterMode, TextureWrapMode.Clamp, name: m_B.name);

            desc.msaaSamples = 1;
            RenderingUtils.ReAllocateIfNeeded(ref m_A.rtResolve, desc, m_FilterMode, TextureWrapMode.Clamp, name: m_A.name);
            RenderingUtils.ReAllocateIfNeeded(ref m_B.rtResolve, desc, m_FilterMode, TextureWrapMode.Clamp, name: m_B.name);
            cmd.SetGlobalTexture(m_A.name, m_A.rtResolve);
            cmd.SetGlobalTexture(m_B.name, m_B.rtResolve);
        }

        public void Clear()
        {
            m_AisBackBuffer = true;
            m_AllowMSAA = m_A.msaa > 1 || m_B.msaa > 1;
        }

        public void SetCameraSettings(RenderTextureDescriptor desc, FilterMode filterMode)
        {
            desc.depthBufferBits = 0;
            m_Desc = desc;
            m_FilterMode = filterMode;

            m_A.msaa = m_Desc.msaaSamples;
            m_B.msaa = m_Desc.msaaSamples;

            if (m_Desc.msaaSamples > 1)
                EnableMSAA(true);
        }

        public RTHandle GetBufferA()
        {
            return (m_AllowMSAA && m_A.msaa > 1) ? m_A.rtMSAA : m_A.rtResolve;
        }

        public void EnableMSAA(bool enable)
        {
            m_AllowMSAA = enable;
            if (enable)
            {
                m_A.msaa = m_Desc.msaaSamples;
                m_B.msaa = m_Desc.msaaSamples;
            }
        }
    }
}
