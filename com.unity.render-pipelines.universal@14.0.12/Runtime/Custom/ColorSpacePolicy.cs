using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// *URP14Port-Phase3-B07FIX-AndroidSrgb：最终上屏的 sRGB 编码策略（跨平台显式化）。
    ///
    /// 背景：D1-a 的 gamma 链把「场景降分辨率 + gamma 空间 UI 混合 + 末端还原成 Linear」
    /// 串成一条链，链尾【Linear 值】需要被编码一次才能正确显示。
    /// 这一次编码由谁做，在不同平台并不一致：
    ///   · 桌面 D3D11/Metal：backbuffer 是 sRGB swapchain，写入时硬件自动编码 ⇒ 管线不再手动编码；
    ///   · GLES/Vulkan：默认帧缓冲写入是否自动编码，取决于设备/驱动，
    ///     而 Unity 的判据（Display.main.requiresSrgbBlitToBackbuffer）实测不可信
    ///     （OnePlus KB2000 / Android 14 / OpenGLES3 上返回 false）。
    /// ⇒ 与其在代码里猜平台，不如把策略显式化，由使用者按目标设备选定，并保证
    ///   「Ubber（UI PP 开）」与「FinalBlit（UI PP 关）」两条出画路径共用同一判定。
    ///
    /// 模式：
                    // Auto / Off：不由管线做最终编码 —— 交给 backbuffer 硬件编码
    ///            实测（OnePlus KB2000 / Android 14 / OpenGLES3）：backbuffer 写入时
    ///            确实会做 sRGB 编码 —— 手动再补一次会变成双重编码（整屏泛白），
    ///            因此安卓与桌面采用同一策略：不由管线做最终编码。
    ///   Manual = 手动补一次编码（缓冲为 Linear 时），用于 backbuffer 不自动编码的平台。
    ///   Off    = 强制不编码（诊断用，行为同 Auto）。
    /// </summary>
    public static class ColorSpacePolicy
    {
        public enum FinalEncodeMode
        {
            Auto = 0,   // 桌面行为：不手动编码
            Manual = 1, // 手动补一次（缓冲为 Linear 时）
            Off = 2,    // 强制不编码
        }

        const string k_PrefsKey = "URP14.FinalEncodeMode";

        static FinalEncodeMode s_Mode = (FinalEncodeMode)(-1);

        /// <summary>当前策略（持久化）。</summary>
        public static FinalEncodeMode Mode
        {
            get
            {
                if (s_Mode == (FinalEncodeMode)(-1))
                {
                    int v = PlayerPrefs.GetInt(k_PrefsKey, (int)FinalEncodeMode.Auto);
                    s_Mode = (FinalEncodeMode)Mathf.Clamp(v, 0, 2);
                }
                return s_Mode;
            }
            set
            {
                s_Mode = value;
                PlayerPrefs.SetInt(k_PrefsKey, (int)value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// 最终出画是否需要手动做一次 sRGB 编码。
        /// bufferIsLinear = 出画瞬间缓冲里是不是 Linear 值（gamma 链回程后为 true）。
        /// </summary>
        public static bool ShouldEncodeFinal(bool bufferIsLinear, bool platformRequiresManualEncode)
        {
            switch (Mode)
            {
                case FinalEncodeMode.Manual:
                    // 手动补一次（仅当缓冲确为 Linear 时，避免把 Gamma 值再编一次）
                    return bufferIsLinear;
                default:
                    // Auto / Off：不由管线做最终编码 —— 交给 backbuffer 硬件编码
                    // （桌面 D3D11 与安卓 GLES3 实测均会编码）。
                    return false;
            }
        }

        /// <summary>该平台是否「可能」需要手动编码（Auto 模式下的兜底判据）。</summary>
        public static bool PlatformRequiresManualEncode()
        {
            var t = SystemInfo.graphicsDeviceType;
            // GLES/Vulkan 的默认帧缓冲写入不保证做 sRGB 编码；
            // D3D11/12（sRGB swapchain）与桌面 Metal 由硬件完成。
            return t == GraphicsDeviceType.OpenGLES2
                || t == GraphicsDeviceType.OpenGLES3
                || t == GraphicsDeviceType.Vulkan;
        }
    }
}
