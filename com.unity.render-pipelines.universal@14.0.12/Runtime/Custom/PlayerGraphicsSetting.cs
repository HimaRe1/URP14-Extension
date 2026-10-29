using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 分辨率档位，数值对应 <see cref="PlayerGraphicsLogic.ResolutionTable"/> 的下标。
/// </summary>
public enum ResolutionLevel
{
    HIGH = 0,
    NORMAL,
    LOW,
    VERYLOW,
}

/// <summary>
/// 抗锯齿挂在谁身上。
/// 实测结论：Overlay 相机的 CameraData 是 base 的拷贝（UniversalRenderPipeline.cs:630），
/// 而 InitializeAdditionalCameraData(:977) 不写 antialiasing —— 所以 **挂在 Overlay(UICamera) 上的 AA 永远不会被读取**。
/// 默认挂 MainCamera（Base），overlay 会继承它。保留 UICamera 选项纯粹是为了做对照实验。
/// </summary>
public enum AATarget
{
    UICamera = 0,
    MainCamera,
}

/// <summary>
/// 图形设置数据层：只负责「存什么」，不负责「怎么生效」，也不碰界面。
/// 生效逻辑在 <see cref="PlayerGraphicsLogic"/>，界面在 <see cref="PlayerGraphicsUI"/>。
/// </summary>
public class PlayerGraphicsSetting : MonoBehaviour
{
    const string kKeyPrefix = "PG2_";
    const string kSavedKey = "PG2_Saved";

    [Header("运行时")]
    public int frameRate = 60;
    public ResolutionLevel resolutionLevel = ResolutionLevel.HIGH;
    public bool displayFPS = true;

    [Header("URP Asset（即时生效）")]
    public float renderScale = 1.0f;
    public int msaaSampleCount = 1;
    public UpscalingFilterSelection upscalingFilter = UpscalingFilterSelection.Linear;

    [Header("魔改管线开关（UniversalRenderer 静态字段）")]
    public bool sUISplitEnable = true;
    public bool sEnableUICameraUseSwapBuffer = true;
    public bool sIsGammaCorrectEnable = true;

    [Header("相机")]
    public AntialiasingMode antialiasing = AntialiasingMode.None;
    public AATarget aaTarget = AATarget.MainCamera;
    public bool uiCameraPostProcessing = false;
    public bool mainCameraPostProcessing = true;

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        Load();
    }

    void OnApplicationQuit()
    {
        Save();
    }

    void OnDestroy()
    {
        Save();
    }

    public void ResetToDefault()
    {
        frameRate = 60;
        resolutionLevel = ResolutionLevel.HIGH;
        displayFPS = true;
        renderScale = 1.0f;
        msaaSampleCount = 1;
        upscalingFilter = UpscalingFilterSelection.Linear;
        sUISplitEnable = true;
        sEnableUICameraUseSwapBuffer = true;
        sIsGammaCorrectEnable = true;
        antialiasing = AntialiasingMode.None;
        aaTarget = AATarget.MainCamera;
        uiCameraPostProcessing = false;
        mainCameraPostProcessing = true;
    }

    public void Save()
    {
        PlayerPrefs.SetInt(kSavedKey, 1);
        SetInt("frameRate", frameRate);
        SetInt("resolutionLevel", (int)resolutionLevel);
        SetBool("displayFPS", displayFPS);
        SetFloat("renderScale", renderScale);
        SetInt("msaaSampleCount", msaaSampleCount);
        SetInt("upscalingFilter", (int)upscalingFilter);
        SetBool("sUISplitEnable", sUISplitEnable);
        SetBool("sEnableUICameraUseSwapBuffer", sEnableUICameraUseSwapBuffer);
        SetBool("sIsGammaCorrectEnable", sIsGammaCorrectEnable);
        SetInt("antialiasing", (int)antialiasing);
        SetInt("aaTarget", (int)aaTarget);
        SetBool("uiCameraPostProcessing", uiCameraPostProcessing);
        SetBool("mainCameraPostProcessing", mainCameraPostProcessing);
        PlayerPrefs.Save();
    }

    public void Load()
    {
        if (!PlayerPrefs.HasKey(kSavedKey))
            return;

        frameRate = GetInt("frameRate", frameRate);
        resolutionLevel = (ResolutionLevel)GetInt("resolutionLevel", (int)resolutionLevel);
        displayFPS = GetBool("displayFPS", displayFPS);
        renderScale = GetFloat("renderScale", renderScale);
        msaaSampleCount = GetInt("msaaSampleCount", msaaSampleCount);
        upscalingFilter = (UpscalingFilterSelection)GetInt("upscalingFilter", (int)upscalingFilter);
        sUISplitEnable = GetBool("sUISplitEnable", sUISplitEnable);
        sEnableUICameraUseSwapBuffer = GetBool("sEnableUICameraUseSwapBuffer", sEnableUICameraUseSwapBuffer);
        sIsGammaCorrectEnable = GetBool("sIsGammaCorrectEnable", sIsGammaCorrectEnable);
        antialiasing = (AntialiasingMode)GetInt("antialiasing", (int)antialiasing);
        aaTarget = (AATarget)GetInt("aaTarget", (int)aaTarget);
        uiCameraPostProcessing = GetBool("uiCameraPostProcessing", uiCameraPostProcessing);
        mainCameraPostProcessing = GetBool("mainCameraPostProcessing", mainCameraPostProcessing);
    }

    static void SetInt(string key, int v) { PlayerPrefs.SetInt(kKeyPrefix + key, v); }
    static void SetFloat(string key, float v) { PlayerPrefs.SetFloat(kKeyPrefix + key, v); }
    static void SetBool(string key, bool v) { PlayerPrefs.SetInt(kKeyPrefix + key, v ? 1 : 0); }

    static int GetInt(string key, int fallback)
    {
        return PlayerPrefs.HasKey(kKeyPrefix + key) ? PlayerPrefs.GetInt(kKeyPrefix + key) : fallback;
    }

    static float GetFloat(string key, float fallback)
    {
        return PlayerPrefs.HasKey(kKeyPrefix + key) ? PlayerPrefs.GetFloat(kKeyPrefix + key) : fallback;
    }

    static bool GetBool(string key, bool fallback)
    {
        return PlayerPrefs.HasKey(kKeyPrefix + key) ? PlayerPrefs.GetInt(kKeyPrefix + key) != 0 : fallback;
    }
}
