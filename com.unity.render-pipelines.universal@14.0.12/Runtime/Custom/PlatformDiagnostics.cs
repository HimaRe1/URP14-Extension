using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // *URP14Port-B07FIX-DIAG：移动端平台事实取证（一次性日志 + 手动触发，adb logcat 可见）。
    // 背景：桌面验证的全部颜色链记账依赖两个平台前提——
    //   ① backbuffer 写入有硬件 sRGB 编码（D3D11 桌面成立）；
    //   ② Display.main.requiresSrgbBlitToBackbuffer = false（桌面实测；2022.3 无 SystemInfo 版 API，
    //      安卓上此值是否可信需设备取证——GLES/Vulkan 的 backbuffer 语义以 deviceType 辅助判断）。
    // 启动时自动打一条；错过或没看到时，F1 面板「打印当前配置」按钮随时补打。定位完成后可整体删除。
    internal static class PlatformDiagnostics
    {
        // *URP14Port-B07FIX-DIAG：版本标识——logcat 里看到 DIAG_V几 就说明跑的是哪个构建，
        // 避免"改了管线但测的是旧包"的错位。
        internal const string Version = "DIAG_V5"; // V5 = FinalBlit 实参 + D1a 缓冲尺寸探针（rs≠1 泛白专查）

        // *URP14Port-B07FIX-DIAG：Uber sRGB 编码运行时开关（-1 自动 / 0 强制关 / 1 强制开）。
        // 用于跨平台颜色对拍：ScreenProbe 按 A/B/C/D 四种模式各抓一帧原始帧缓冲并给出数值。
        public static int DebugUberSrgb = -1;

        static bool s_LoggedSrgb;
        static int s_LastSrgb = -1; // -1 = 尚未采集（FinalBlitPass 还没跑过）
        static int s_ResolveLogBase;
        static int s_ResolveLogUi;
        static int s_D1aCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => DumpNow();

        // 出画判定全量取证：每相机前 3 帧 + 每 300 帧周期重复（连接时机无关，随时能抓到）。
        internal static void LogResolveState(bool isUi, bool applyPost, bool finalPost, bool resolveToTarget,
            bool doSrgb, bool resolved, bool bufferLinear, int frame, string fmt, bool hdrEnabled)
        {
            int count = isUi ? ++s_ResolveLogUi : ++s_ResolveLogBase;
            if (count > 3 && count % 300 != 0) return;
            Debug.Log("[PlatformDiag] Resolve " + (isUi ? "[UI相机]" : "[Base相机]")
                + " count=" + count + " frame=" + frame
                + " applyPost=" + applyPost
                + " finalPost=" + finalPost
                + " uberResolveToScreen=" + resolveToTarget
                + " uberDoSRGB=" + doSrgb
                + " cameraTargetResolved=" + resolved
                + " bufferLinear=" + bufferLinear
                + " colorFmt=" + fmt
                + " hdrEnabled=" + hdrEnabled);
        }

        // D1a BlitPass（Upscale/Line2Gamma）执行证明：首次 + 每 300 帧周期重复。
        internal static void LogD1aOnce(string transform, float rs)
        {
            s_D1aCount++;
            if (s_D1aCount != 1 && s_D1aCount % 300 != 0) return;
            Debug.Log("[PlatformDiag] D1a BlitPass ran count=" + s_D1aCount
                + " transform=" + transform + " rs=" + rs.ToString("0.00"));
        }

        // *URP14Port-B07FIX-DIAG（rs≠1 泛白专查）：D1a 各步 back→front 的缓冲尺寸。
        // 若 src/dst 跨尺寸（或 desc 与实际不符），Blit/解码就发生在错误大小的缓冲上。
        static int s_D1aSizeCount;
        internal static void LogD1aSizes(string tag, int srcW, int srcH, int dstW, int dstH, int descW, int descH)
        {
            s_D1aSizeCount++;
            if (s_D1aSizeCount > 6 && s_D1aSizeCount % 300 != 0) return;
            Debug.Log("[PlatformDiag] D1aSize(" + Version + ") " + tag
                + " src=" + srcW + "x" + srcH
                + " dst=" + dstW + "x" + dstH
                + " desc=" + descW + "x" + descH);
        }

        // *URP14Port-B07FIX-DIAG（rs≠1 泛白专查）：FinalBlit 的实际参数与 source 尺寸。
        // 判别：isLine/bufferIsLinear/encodeFinal 的组合 vs source 尺寸——
        // 「双重编码」= 组合正确但画面泛白；「坏句柄/未初始化」= source 尺寸异常。
        static int s_FinalBlitBase;
        static int s_FinalBlitUi;
        internal static void LogFinalBlit(bool isUi, int srcW, int srcH,
            bool isLine, bool bufferIsLinear, bool encodeFinal, string policy)
        {
            int count = isUi ? ++s_FinalBlitUi : ++s_FinalBlitBase;
            if (count > 3 && count % 300 != 0) return;
            Debug.Log("[PlatformDiag] FinalBlit(" + Version + ") " + (isUi ? "[UI相机]" : "[Base相机]")
                + " count=" + count
                + " src=" + srcW + "x" + srcH
                + " isLine=" + isLine
                + " bufferIsLinear=" + bufferIsLinear
                + " encodeFinal=" + encodeFinal
                + " policy=" + policy);
        }

        // FinalBlitPass.InitPassData 时调用：渲染时真实的 requireSrgbConversion 取值。
        internal static void LogSrgbOnce(bool requireSrgbConversion)
        {
            s_LastSrgb = requireSrgbConversion ? 1 : 0;
            if (s_LoggedSrgb) return;
            s_LoggedSrgb = true;
            Debug.Log("[PlatformDiag] cameraData.requireSrgbConversion=" + requireSrgbConversion);
        }

        // 手动触发（面板「打印当前配置」按钮）：不依赖启动时机，随时取证。
        public static void DumpNow()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            string assetInfo = "null";
            if (asset != null)
                assetInfo = asset.name + " HDR=" + asset.supportsHDR
                    + " MSAA=" + asset.msaaSampleCount
                    + " renderScale=" + asset.renderScale.ToString("0.00");
            string srgb = s_LastSrgb < 0 ? "未采集(渲染未跑)" : (s_LastSrgb == 1).ToString();
            // UI 绘制链路诊断：UI 由哪台相机渲染（renderMode）？用的是哪个 shader？
            // ★关键：UI 的 gamma 预编码只存在于我们改过的 shader（UI_Pixel.hlsl 的 _IsInUICamera 分支）；
            //   若 UI 用的是原生 URP UI / TMP 其它 variant ⇒ 没有预编码 ⇒ gamma 链开启后 UI 会被多解码一次而变暗。
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                Debug.Log("[PlatformDiag] Canvas '" + canvas.name + "' mode=" + canvas.renderMode
                    + " worldCamera=" + (canvas.worldCamera != null ? canvas.worldCamera.name : "null")
                    + " sortOrder=" + canvas.sortingOrder);
                var crs = canvas.GetComponentsInChildren<CanvasRenderer>();
                for (int i = 0; i < crs.Length; i++)
                {
                    // CanvasRenderer 在不同 Unity 版本里 material 的暴露方式不同（属性/GetMaterial）
                    // ⇒ 用反射取，取不到只影响这行诊断，不影响其它。
                    Material mat = null;
                    try
                    {
                        var pi = typeof(CanvasRenderer).GetProperty("material",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (pi != null) mat = pi.GetValue(crs[i]) as Material;
                        if (mat == null)
                        {
                            var mi = typeof(CanvasRenderer).GetMethod("GetMaterial",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                                null, System.Type.EmptyTypes, null);
                            if (mi != null) mat = mi.Invoke(crs[i], null) as Material;
                        }
                    }
                    catch { }
                    var sh = mat != null ? mat.shader : null;
                    // Shader 没有 HasProperty API；用 shader 名判定是否为【我们改过的那份】
                    // （UI/Default 带 _IsInUICamera 预编码分支；TextMeshPro/Mobile/Distance Field 亦已覆盖）。
                    string shName = sh != null ? sh.name : "null";
                    bool ours = shName == "UI/Default" || shName.StartsWith("TextMeshPro/");
                    Debug.Log("[PlatformDiag]   UI '" + crs[i].name + "' shader=" + shName + " oursWithGammaPreEncode=" + ours);
                }
            }

            // 全相机真实 PP 状态：面板勾选 ≠ 相机上真的有值，设备自证
            foreach (var cam in Camera.allCameras)
            {
                var a = cam.GetUniversalAdditionalCameraData();
                Debug.Log("[PlatformDiag] Cam '" + cam.name + "' tag=" + cam.tag
                    + " type=" + a.renderType
                    + " renderPostProcessing=" + a.renderPostProcessing);
            }
            // XPP shader 裁剪探针：Shader.Find 在真机上找不到 = 已被 strip（Editor 不会裁）
            var probe = Shader.Find("Hidden/PostProcessing/Glitch/RGBSplitV5");
            Debug.Log("[PlatformDiag] " + Version
                + " device=" + SystemInfo.graphicsDeviceType
                + " colorSpace=" + QualitySettings.activeColorSpace
                + " Display.main.requiresSrgbBlitToBackbuffer=" + Display.main.requiresSrgbBlitToBackbuffer
                + " quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                + " urpAsset=" + assetInfo
                + " requireSrgbConversion=" + srgb
                + " xppShaderFind=" + (probe != null ? "OK" : "NULL(被裁剪!)")
                + " deviceHDRFormat=" + SystemInfo.GetGraphicsFormat(DefaultFormat.HDR)
                + " deviceLDRFormat=" + SystemInfo.GetGraphicsFormat(DefaultFormat.LDR)
                + " hdrDisplayLuminanceInfo=见 Resolve 行的 maxToneMapLuminance");
        }
    }
}
