using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 图形设置执行层：把 <see cref="PlayerGraphicsSetting"/> 的数据真正应用到管线。
/// 覆盖四类目标：运行时（帧率 / 屏幕分辨率）、URP Asset（renderScale / MSAA / 上采样）、
/// 魔改静态开关（UniversalRenderer）、相机（抗锯齿归属 / 后处理开关）。
/// </summary>
public class PlayerGraphicsLogic : MonoBehaviour
{
    /// <summary>分辨率档位对应的长边像素（另一边按当前屏幕宽高比换算）。</summary>
    public static readonly int[] ResolutionTable = { 1920, 1334, 1024, 800 };

    public static readonly float[] RenderScalePresets = { 0.5f, 0.66f, 0.75f, 1.0f };
    public static readonly int[] MsaaPresets = { 1, 2, 4, 8 };

    const float kFpsInterval = 0.5f;
    const string kUICameraTag = "UICamera";

    UniversalRenderPipelineAsset _asset;
    Camera _mainCamera;
    Camera _uiCamera;
    UniversalAdditionalCameraData _mainData;
    UniversalAdditionalCameraData _uiData;

    int _appliedWidth = -1;
    int _appliedHeight = -1;
    float _fpsTimer;
    int _fpsFrames;

    public float Fps { get; private set; }
    public float FrameMs { get; private set; }

    public UniversalRenderPipelineAsset Asset
    {
        get
        {
            if (_asset == null)
                RefreshAsset();
            return _asset;
        }
    }

    public Camera MainCamera { get { return _mainCamera; } }
    public Camera UICamera { get { return _uiCamera; } }

    void Update()
    {
        _fpsTimer += Time.unscaledDeltaTime;
        _fpsFrames++;
        if (_fpsTimer >= kFpsInterval)
        {
            Fps = _fpsFrames / _fpsTimer;
            FrameMs = _fpsTimer * 1000f / _fpsFrames;
            _fpsTimer = 0f;
            _fpsFrames = 0;
        }
    }

    public void RefreshAsset()
    {
        _asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (_asset == null)
            _asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
    }

    /// <summary>
    /// 重新查找 MainCamera 与 UICamera。切场景后需要重新调用。
    /// </summary>
    public void RefreshCameras()
    {
        _mainCamera = Camera.main;
        _mainData = GetAdditionalData(_mainCamera);

        _uiCamera = null;
        _uiData = null;
        GameObject uiGo = null;
        try
        {
            uiGo = GameObject.FindGameObjectWithTag(kUICameraTag);
        }
        catch (UnityException)
        {
            // 工程里没有定义 UICamera tag：这不是错误，只是本魔改管线用不上，面板会提示。
        }

        if (uiGo != null)
        {
            _uiCamera = uiGo.GetComponent<Camera>();
            _uiData = GetAdditionalData(_uiCamera);
        }
    }

    static UniversalAdditionalCameraData GetAdditionalData(Camera cam)
    {
        if (cam == null)
            return null;
        UniversalAdditionalCameraData data;
        cam.TryGetComponent(out data);
        return data;
    }

    /// <summary>把设置整体应用一次。面板任何改动后调用即可。</summary>
    public void Apply(PlayerGraphicsSetting s)
    {
        if (s == null)
            return;

        Application.targetFrameRate = s.frameRate;
        ApplyResolution(s);

        RefreshAsset();
        if (_asset != null)
        {
            _asset.renderScale = s.renderScale;
            _asset.msaaSampleCount = s.msaaSampleCount;
            _asset.upscalingFilter = s.upscalingFilter;
        }

        // 魔改管线三件套：UI/场景分离总闸、swap buffer、Gamma 矫正
        UniversalRenderer.sUISplitEnable = s.sUISplitEnable;
        UniversalRenderer.sEnableUICameraUseSwapBuffer = s.sEnableUICameraUseSwapBuffer;
        UniversalRenderer.sIsGammaCorrectEnable = s.sIsGammaCorrectEnable;

        RefreshCameras();

        // *URP14Port-Phase3-B07FIX（2026-10-29，AA 路由修复）：抗锯齿**恒挂基底相机**，UI 相机恒 None。
        // 原逻辑按 s.aaTarget 路由——aaTarget 是 PlayerPrefs 持久化的，历史上只要点过一次
        // 「AA 挂在哪 → UI相机」就会一直把 AA 写到 UI 相机上；而 UI 相机是 Overlay
        //（继承 base 的 AA，自身 additionalData 的值根本不被读取）⇒ 面板点 FXAA/SMAA 后
        // base 相机永远不变，即「AA 无效」的直接原因。aaTarget 字段与旧存档保留但不再读取。
        // UI 相机恒 None 的语义不变（防同帧跑两遍 AA）。
        ApplyCameraData(_uiData, AntialiasingMode.None, s.uiCameraPostProcessing);
        ApplyCameraData(_mainData, s.antialiasing, s.mainCameraPostProcessing);
        if (_mainData == null)
            Debug.LogWarning("[PlayerGraphicsLogic] 未找到基底相机的附加数据（Camera.main 依赖 MainCamera Tag），抗锯齿未应用");
    }

    static void ApplyCameraData(UniversalAdditionalCameraData data, AntialiasingMode aa, bool postProcessing)
    {
        if (data == null)
            return;
        data.antialiasing = aa;
        data.antialiasingQuality = AntialiasingQuality.Medium;
        data.renderPostProcessing = postProcessing;
    }

    void ApplyResolution(PlayerGraphicsSetting s)
    {
        int longEdge = ResolutionTable[(int)s.resolutionLevel];
        int w, h;
        if (Screen.width > Screen.height)
        {
            w = longEdge;
            h = longEdge * Screen.height / Screen.width;
        }
        else
        {
            w = longEdge * Screen.width / Screen.height;
            h = longEdge;
        }

        // Screen.SetResolution 会重建 backbuffer，只在真的变了才调用，避免每帧闪烁
        if (w == _appliedWidth && h == _appliedHeight)
            return;

        _appliedWidth = w;
        _appliedHeight = h;
        Screen.SetResolution(w, h, FullScreenMode.Windowed);
    }

    /// <summary>把当前真实生效的管线状态打成文本，方便对照实验时贴出来。</summary>
    public string Dump(PlayerGraphicsSetting s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== URP Pipeline Debug Dump ====");
        // *URP14Port-Phase3-B07：2022.3 将 Resolution.refreshRate 标记为 obsolete（建议 refreshRateRatio），
        // 该 API 仅用于调试 Dump 文本；保持与 URP12 源逐字节一致的取值逻辑，仅压制过时警告。
#pragma warning disable 0618
        sb.AppendLine("screen        : " + Screen.width + " x " + Screen.height + "  @ " + Screen.currentResolution.refreshRate + "Hz");
#pragma warning restore 0618
        sb.AppendLine("targetFPS     : " + Application.targetFrameRate + "   measured: " + Fps.ToString("F1") + " (" + FrameMs.ToString("F2") + " ms)");

        var asset = Asset;
        if (asset == null)
        {
            sb.AppendLine("URP Asset     : <null> —— 没找到 UniversalRenderPipelineAsset");
        }
        else
        {
            float rs = asset.renderScale;
            sb.AppendLine("renderScale   : " + rs + "  -> scene RT " + Mathf.RoundToInt(Screen.width * rs) + " x " + Mathf.RoundToInt(Screen.height * rs));
            sb.AppendLine("msaa          : " + asset.msaaSampleCount + "x");
            sb.AppendLine("upscaling     : " + asset.upscalingFilter);
            sb.AppendLine("hdr           : " + asset.supportsHDR);
        }

        sb.AppendLine("sUISplitEnable: " + UniversalRenderer.sUISplitEnable);
        sb.AppendLine("swapBuffer    : " + UniversalRenderer.sEnableUICameraUseSwapBuffer);
        sb.AppendLine("gammaCorrect  : " + UniversalRenderer.sIsGammaCorrectEnable);

        RefreshCameras();
        AppendCamera(sb, "MainCamera", _mainCamera, _mainData);
        AppendCamera(sb, "UICamera", _uiCamera, _uiData);

        if (s != null)
            sb.AppendLine("aaTarget      : " + s.aaTarget + "  mode " + s.antialiasing);

        return sb.ToString();
    }

    static void AppendCamera(StringBuilder sb, string label, Camera cam, UniversalAdditionalCameraData data)
    {
        if (cam == null)
        {
            sb.AppendLine(label + " : <not found>");
            return;
        }

        string type = data == null ? "?" : (data.renderType == CameraRenderType.Base ? "Base" : "Overlay");
        string aa = data == null ? "?" : data.antialiasing.ToString();
        string pp = data == null ? "?" : (data.renderPostProcessing ? "on" : "off");
        sb.AppendLine(label + " : " + type + "  AA=" + aa + "  PP=" + pp + "  cullingMask=" + cam.cullingMask);
    }
}
