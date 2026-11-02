using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// *URP14Port-B07FIX-DIAG：颜色探针的时序驱动（PC / 安卓通用）。
// 本版直接抓【最终上屏内容】：
//   1) 首选 ScreenCapture.CaptureScreenshotAsTexture()（拿到的就是屏幕像素，无文件系统依赖）；
//   2) 不支持时回退到 CaptureScreenshot(相对路径) 落盘再读回。
// 三种 Uber 编码模式（0 自动 / 1 强制关 / 2 强制开）各抓一帧，
// PC 与安卓同口径对比即可判定两端最终输出的编码次数是否一致。
public static class ScreenProbe
{
    // *URP14Port-Phase3-B07FIX-DIAG：探针默认【不自动运行】——它会强制切换 Uber 编码模式，
    // 会干扰正常验收。确需对拍时把下面这行取消注释（或用 ColorProbe.Enabled 手动打开）。
    // [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    // static void Bootstrap()
    // {
    //     Debug.Log("[ScreenProbe] bootstrap");
    //     var go = new GameObject("~ScreenProbe");
    //     Object.DontDestroyOnLoad(go);
    //     go.hideFlags = HideFlags.HideAndDontSave;
    //     go.AddComponent<ScreenProbeRunner>();
    // }
}

internal class ScreenProbeRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        Debug.Log("[ScreenProbe] runner started");
        yield return new WaitForSecondsRealtime(4f);

        ColorProbe.Enabled = true;

        for (int mode = 0; mode <= 2; mode++)
        {
            ColorProbe.Mode = mode;
            yield return new WaitForSecondsRealtime(2.5f);
            yield return Capture("SCREEN-mode" + mode);
        }
        ColorProbe.Mode = 0;
        yield return new WaitForSecondsRealtime(2.5f);
        yield return Capture("SCREEN-mode0-restore");

        ColorProbe.Enabled = false;
        Debug.Log("[ScreenProbe] probe done");
    }

    IEnumerator Capture(string tag)
    {
        // 方案 1：直接抓屏幕（需在帧末调用）
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch { }
        if (tex != null)
        {
            ColorProbe.Analyze(tag, tex.GetPixels32(), " src=screenTex");
            Destroy(tex);
            yield break;
        }
        Debug.Log("[ScreenProbe] " + tag + " CaptureScreenshotAsTexture 不可用，回退文件抓取");

        // 方案 2：落盘（相对路径，Unity 会拼到 persistentDataPath 下）
        string fileName = "probe_" + tag + ".png";
        string abs = Path.Combine(Application.persistentDataPath, fileName);
        try { if (File.Exists(abs)) File.Delete(abs); } catch { }
        ScreenCapture.CaptureScreenshot(fileName, 1);

        float deadline = Time.realtimeSinceStartup + 15f;
        while (Time.realtimeSinceStartup < deadline)
        {
            yield return new WaitForSecondsRealtime(0.3f);
            if (!File.Exists(abs)) continue;
            byte[] bytes = null;
            try { bytes = File.ReadAllBytes(abs); } catch (System.Exception e) { Debug.Log("[ScreenProbe] read fail " + e.Message); continue; }
            if (bytes == null || bytes.Length < 1024) continue;
            var t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!t2.LoadImage(bytes)) { Destroy(t2); continue; }
            ColorProbe.Analyze(tag, t2.GetPixels32(), " src=screenFile");
            Destroy(t2);
            yield break;
        }
        Debug.Log("[ScreenProbe] " + tag + " capture failed");
    }
}
