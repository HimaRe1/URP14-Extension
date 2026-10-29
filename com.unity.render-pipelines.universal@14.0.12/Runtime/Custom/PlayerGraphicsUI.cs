using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 图形设置界面：运行时自举的调试面板（IMGUI），不依赖任何场景内预制体。
/// 按 F1 显示 / 隐藏，A- / A+ 调整面板与字号缩放。
///
/// 面板上每一项的语义都对应魔改管线的真实开关，设计目的就是做对照实验：
///   · renderScale 变化 → 观察「场景糊、UI 不糊」是否成立（需要 sUISplitEnable = true）
///   · sUISplitEnable 关掉 → UI 应该跟场景一起糊（这是该需求的决定性验证）
///   · swap / gamma 组合 → 观察发白 / 发暗
///   · AA 必须挂 MainCamera：Overlay 相机继承 base 的 AA（UniversalRenderPipeline.cs:889），
///     挂 UI 相机等于没挂。swap 开 → FXAA 跑在栈尾 UI 相机；swap 关 → FXAA 由基底相机跑（只抗场景）
/// </summary>
[RequireComponent(typeof(PlayerGraphicsSetting), typeof(PlayerGraphicsLogic))]
public class PlayerGraphicsUI : MonoBehaviour
{
    static readonly int[] FrameRatePresets = { 30, 45, 60, 120 };

    // MSAA 行暂时隐藏（2026-10-16）。当前硬前提是「MSAA 必须保持 Disabled」：
    // 开 4x 会让 UI 消失，或报 "non-multisampled texture bound to multisampled sampler"
    // （根因见 fix7：CopyDepthPass 仍从 cameraData 读到 4x 而 _CameraDepthAttachment 已按 1x 创建）。
    // 等 MSAA 正式排期时，把这个翻成 true 面板就会重新显示这一行。
    static readonly bool kShowMsaaRow = false;

    // 以 1080p 为基准，实际按屏幕高度自动缩放，再乘玩家手动缩放
    const float kBaseWidth = 470f;
    const float kBaseFont = 16f;
    const float kBaseRow = 30f;
    const float kBaseLabel = 132f;

    PlayerGraphicsSetting _setting;
    PlayerGraphicsLogic _logic;
    Vector2 _scroll;
    bool _visible = true;

    float _userScale = 1f;      // 玩家手动缩放（A- / A+）
    float _s = 1f;              // 最终缩放系数
    int _font;
    float _rowH;
    float _labelW;
    float _panelW;

    GUIStyle _labelStyle;
    GUIStyle _titleStyle;
    GUIStyle _buttonStyle;
    GUIStyle _toggleStyle;

    void Start()
    {
        _setting = GetComponent<PlayerGraphicsSetting>();
        _logic = GetComponent<PlayerGraphicsLogic>();
        _logic.Apply(_setting);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1))
            _visible = !_visible;
    }

    void OnGUI()
    {
        if (!_visible || _setting == null || _logic == null)
            return;

        EnsureStyles();

        float pad = 8f * _s;
        _panelW = kBaseWidth * _s;
        float h = Mathf.Min(Screen.height - pad * 2f, 1040f * _s);
        Rect r = new Rect(pad, pad, _panelW, h);

        DrawBackground(r);

        GUILayout.BeginArea(r);
        _scroll = GUILayout.BeginScrollView(_scroll,
            GUILayout.Width(_panelW - 4f * _s), GUILayout.Height(h - 4f * _s));

        DrawStatus();
        DrawResolution();
        DrawPipelineAsset();
        DrawModSwitches();
        DrawCamera();
        DrawActions();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    /// <summary>黑色半透明底 + 一圈细边框，避免文字糊在场景上看不清。</summary>
    void DrawBackground(Rect r)
    {
        Color old = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.78f);
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill);

        float t = Mathf.Max(1f, _s);
        GUI.color = new Color(1f, 1f, 1f, 0.22f);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture, ScaleMode.StretchToFill);
        GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture, ScaleMode.StretchToFill);
        GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture, ScaleMode.StretchToFill);
        GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture, ScaleMode.StretchToFill);

        GUI.color = old;
    }

    void EnsureStyles()
    {
        float want = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f) * _userScale;
        if (_labelStyle != null && Mathf.Approximately(want, _s))
            return;

        _s = want;
        _font = Mathf.RoundToInt(kBaseFont * _s);
        _rowH = kBaseRow * _s;
        _labelW = kBaseLabel * _s;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = _font,
            richText = true,
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft,
        };
        _labelStyle.normal.textColor = Color.white;
        _labelStyle.padding = new RectOffset(2, 2, 1, 1);

        _titleStyle = new GUIStyle(_labelStyle) { fontStyle = FontStyle.Bold };
        _titleStyle.normal.textColor = new Color(1f, 0.90f, 0.45f);

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = _font,
            richText = true,
            fixedHeight = _rowH,
        };
        _buttonStyle.padding = new RectOffset(Mathf.RoundToInt(8 * _s), Mathf.RoundToInt(8 * _s), 2, 2);
        _buttonStyle.normal.textColor = Color.white;

        _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = _font, fixedHeight = _rowH };
        _toggleStyle.normal.textColor = Color.white;
        _toggleStyle.onNormal.textColor = Color.white;
        _toggleStyle.active.textColor = Color.white;
        _toggleStyle.onActive.textColor = Color.white;
    }

    void DrawStatus()
    {
        Title("URP 魔改管线调试面板");
        Label("F1 隐藏 / 显示     A- / A+ 调整字号");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("A-", _buttonStyle, GUILayout.Width(_rowH * 1.7f), GUILayout.Height(_rowH)))
            _userScale = Mathf.Clamp(_userScale - 0.15f, 0.7f, 3f);
        if (GUILayout.Button("A+", _buttonStyle, GUILayout.Width(_rowH * 1.7f), GUILayout.Height(_rowH)))
            _userScale = Mathf.Clamp(_userScale + 0.15f, 0.7f, 3f);
        if (GUILayout.Button("重置缩放", _buttonStyle, GUILayout.Height(_rowH)))
            _userScale = 1f;
        GUILayout.EndHorizontal();

        if (_setting.displayFPS)
            Label("FPS " + _logic.Fps.ToString("F1") + "    帧时间 " + _logic.FrameMs.ToString("F2") + " ms");
        Label("屏幕 " + Screen.width + " x " + Screen.height);

        var asset = _logic.Asset;
        float rs = asset != null ? asset.renderScale : _setting.renderScale;
        Label("场景 RT " + Mathf.RoundToInt(Screen.width * rs) + " x " + Mathf.RoundToInt(Screen.height * rs)
              + "   (renderScale " + rs.ToString("F2") + ")");
        Label("UI   RT " + Screen.width + " x " + Screen.height + "   (分离开启时)");
        if (asset == null)
            Label("<color=#ff8080>未找到 URP Asset</color>");
        Separator();
    }

    void DrawResolution()
    {
        Title("[ 分辨率与帧率 ]");

        GUILayout.BeginHorizontal();
        GUILayout.Label("屏幕档位", _labelStyle, GUILayout.Width(_labelW), GUILayout.Height(_rowH));
        var levels = (ResolutionLevel[])Enum.GetValues(typeof(ResolutionLevel));
        foreach (var lv in levels)
            if (ToggleButton(_setting.resolutionLevel == lv, PlayerGraphicsLogic.ResolutionTable[(int)lv].ToString()))
            {
                _setting.resolutionLevel = lv;
                Changed();
            }
        GUILayout.EndHorizontal();

        DrawIntRow("帧率上限", FrameRatePresets, _setting.frameRate, v => _setting.frameRate = v);
        Separator();
    }

    void DrawPipelineAsset()
    {
        Title("[ URP Asset ]");
        DrawFloatRow("renderScale", PlayerGraphicsLogic.RenderScalePresets, _setting.renderScale,
            v => _setting.renderScale = v, v => v.ToString("F2"));
        if (kShowMsaaRow)
            DrawIntRow("MSAA", PlayerGraphicsLogic.MsaaPresets, _setting.msaaSampleCount,
                v => _setting.msaaSampleCount = v, v => v == 1 ? "关" : v + "x");
        DrawEnumRow("上采样", _setting.upscalingFilter, v => _setting.upscalingFilter = v, UpscalingName);
        if (_setting.upscalingFilter == UpscalingFilterSelection.FSR)
        {
            // FSR 是「三棒 shader」（scalingSetup 转 Gamma2.0 → EASU 上采样 → FinalPost 里 RCAS 锐化），
            // 全在 final pass 里；final pass 在 swap 模式下被关死，所以能不能跑只看 swap 开关。
            if (_setting.sEnableUICameraUseSwapBuffer)
                Label("<color=#ffcc00>FSR 不执行：swap 开时 final pass 进不了队（UR:1144 / UR:1024）。想测 FSR 请关掉 swap</color>");
            else
                Label("<color=#66cc66>FSR 生效：由基底相机跑 final pass（UR:1148 / UR:1192）并直出屏幕，UI 之后全分辨率叠加</color>");
        }
        else if (_setting.upscalingFilter == UpscalingFilterSelection.Auto)
            Label("<color=#ffcc00>Auto 在整数倍缩放（如 rs=0.5）时官方会解析成 NN，非整数倍退回 Bilinear</color>");
        Separator();
    }

    void DrawModSwitches()
    {
        Title("[ 魔改管线开关 ]");
        ToggleRow("sUISplitEnable", ref _setting.sUISplitEnable);
        ToggleRow("swapBuffer", ref _setting.sEnableUICameraUseSwapBuffer);
        ToggleRow("gammaCorrect", ref _setting.sIsGammaCorrectEnable);
        Separator();
    }

    void DrawCamera()
    {
        Title("[ 相机 ]");
        DrawEnumRow("抗锯齿", _setting.antialiasing, v => _setting.antialiasing = v, AaName);
        DrawEnumRow("AA 挂在哪", _setting.aaTarget, v => _setting.aaTarget = v, AaTargetName);
        ToggleRow("UICamera PP", ref _setting.uiCameraPostProcessing);
        ToggleRow("MainCamera PP", ref _setting.mainCameraPostProcessing);

        // Overlay 相机在栈里会直接继承 base 的 AA（UniversalRenderPipeline.cs:889），
        // 它自己 additional data 上设的 AA 根本不会被读到 ⇒ 挂 UI 相机 = 白挂。
        if (_setting.aaTarget == AATarget.UICamera)
            Label("<color=#ffcc00>AA 挂 UI 相机无效：Overlay 继承 base 的 AA（URP:889），请改挂主相机</color>");

        // FXAA 现在两条路径都能跑（:1011 是放行而不是清零）：
        //   · swap 开 → 栈尾 UI 相机的 final pass（UR:1014）
        //   · swap 关 → 基底相机的 final pass（UR:1140），UI 直写屏幕不参与 ⇒ 只抗场景锯齿
        // AA 挂在 UI 相机时下面两条都别报「生效」——Overlay 读到的是 base 的 None。
        bool aaUsable = _setting.aaTarget != AATarget.UICamera;

        if (_setting.antialiasing == AntialiasingMode.FastApproximateAntialiasing && aaUsable)
            Label(_setting.sEnableUICameraUseSwapBuffer
                ? "<color=#66cc66>FXAA 生效：跑在栈尾 UI 相机的 final pass（UR:1014）</color>"
                : "<color=#66cc66>FXAA 生效：由基底相机跑（UR:1140），只抗场景锯齿、UI 保持锐利</color>");

        // SMAA 在 PostProcessPass.Render 里（PP:370），归「被入队的那台相机」管：
        // swap 开 + UI PP 开 → 基底跳过 PP（UR:1152），由 UI 相机跑；否则由基底跑，需要 MainCamera PP。
        if (_setting.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing && aaUsable)
        {
            if (_setting.sEnableUICameraUseSwapBuffer && _setting.uiCameraPostProcessing)
                Label("<color=#66cc66>SMAA 在 UI 相机的后处理里跑（基底已跳过 PP，UR:1152）</color>");
            else if (!_setting.mainCameraPostProcessing)
                Label("<color=#ffcc00>SMAA 属于后处理链：需打开 MainCamera PP（或打开 UICamera PP 让栈尾接管）</color>");
        }
        Separator();
    }

    void DrawActions()
    {
        Title("[ 操作 ]");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("打印当前配置", _buttonStyle, GUILayout.Height(_rowH)))
            Debug.Log(_logic.Dump(_setting));
        if (GUILayout.Button("恢复默认", _buttonStyle, GUILayout.Height(_rowH)))
        {
            _setting.ResetToDefault();
            Changed();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("重新查找相机", _buttonStyle, GUILayout.Height(_rowH)))
            _logic.RefreshCameras();
        if (GUILayout.Button("保存", _buttonStyle, GUILayout.Height(_rowH)))
            _setting.Save();
        GUILayout.EndHorizontal();

        ToggleRow("显示 FPS", ref _setting.displayFPS);

        Label("提示：改完 renderScale 后看 UI 文字是否仍锐利");
        Label("（split 开 = 锐利，split 关 = 跟场景一起糊）");
    }

    void Changed()
    {
        _logic.Apply(_setting);
        _setting.Save();
    }

    // 枚举原名太长（FastApproximateAntialiasing 27 字符），面板上用短名
    static string AaName(AntialiasingMode m)
    {
        switch (m)
        {
            case AntialiasingMode.FastApproximateAntialiasing: return "FXAA";
            case AntialiasingMode.SubpixelMorphologicalAntiAliasing: return "SMAA";
            default: return "关";
        }
    }

    static string AaTargetName(AATarget t)
    {
        return t == AATarget.UICamera ? "UI相机" : "主相机";
    }

    static string UpscalingName(UpscalingFilterSelection f)
    {
        switch (f)
        {
            case UpscalingFilterSelection.Auto: return "Auto";
            case UpscalingFilterSelection.Point: return "NN";
            // FSR 能不能跑完全取决于 swap 开关（见下方提示）：
            //   swap 关 → 基底相机跑 final pass 并直出屏幕（UR:1148 / :1192），FSR 完整生效；
            //   swap 开 → final pass 进不了队（UR:1144 / :1024），FSR 不执行。
            // 所以这里不再置灰，改为按当前模式给出提示，方便实测。
            case UpscalingFilterSelection.FSR: return "FSR";
            default: return "Bilinear";
        }
    }

    // enabledOf：返回 false 的枚举项置灰且不可点，用来标记「管线里还没接上」的选项。
    // 只挡「选它」，不挡「从它切走」—— 已保存为该项时仍能点别的按钮改回来。
    void DrawEnumRow<T>(string label, T current, Action<T> setter, Func<T, string> nameOf = null,
        Func<T, bool> enabledOf = null)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(_labelW), GUILayout.Height(_rowH));
        foreach (T v in Enum.GetValues(typeof(T)))
        {
            bool on = Equals(v, current);
            bool enabled = enabledOf == null || enabledOf(v);
            string text = nameOf != null ? nameOf(v) : v.ToString();

            bool guiWasEnabled = GUI.enabled;
            if (!enabled)
                GUI.enabled = false;
            bool clicked = ToggleButton(on, text);
            GUI.enabled = guiWasEnabled;

            if (clicked && !on && enabled)
            {
                setter(v);
                Changed();
            }
        }
        GUILayout.EndHorizontal();
    }

    void DrawIntRow(string label, int[] values, int current, Action<int> setter, Func<int, string> nameOf = null)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(_labelW), GUILayout.Height(_rowH));
        foreach (int v in values)
        {
            bool on = v == current;
            string text = nameOf != null ? nameOf(v) : v.ToString();
            if (ToggleButton(on, text) && !on)
            {
                setter(v);
                Changed();
            }
        }
        GUILayout.EndHorizontal();
    }

    void DrawFloatRow(string label, float[] values, float current, Action<float> setter, Func<float, string> nameOf)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(_labelW), GUILayout.Height(_rowH));
        foreach (float v in values)
        {
            bool on = Mathf.Approximately(v, current);
            if (ToggleButton(on, nameOf(v)) && !on)
            {
                setter(v);
                Changed();
            }
        }
        GUILayout.EndHorizontal();
    }

    void ToggleRow(string label, ref bool value)
    {
        GUILayout.BeginHorizontal();
        bool newValue = GUILayout.Toggle(value, label, _toggleStyle,
            GUILayout.Width(_panelW - 24f * _s), GUILayout.Height(_rowH));
        GUILayout.EndHorizontal();
        if (newValue != value)
        {
            value = newValue;
            Changed();
        }
    }

    bool ToggleButton(bool on, string text)
    {
        return GUILayout.Toggle(on, text, _buttonStyle,
            GUILayout.Height(_rowH), GUILayout.MinWidth(58f * _s));
    }

    void Title(string text)
    {
        GUILayout.Label(text, _titleStyle, GUILayout.Height(_rowH));
    }

    void Label(string text)
    {
        GUILayout.Label(text, _labelStyle, GUILayout.Height(_rowH));
    }

    void Separator()
    {
        GUILayout.Space(8f * _s);
    }
}
