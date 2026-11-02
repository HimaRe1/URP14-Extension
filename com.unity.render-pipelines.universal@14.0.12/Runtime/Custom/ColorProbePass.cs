using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// *URP14Port-B07FIX-DIAG：颜色数值探针（GPU 回读，不依赖截图/文件系统）。
// 目的：在 PC 与安卓上直接读到【帧缓冲原始像素】，用数值判定编码次数是否一致，
// 避免用手机截图（经设备色彩管理 + JPEG）做判断。
// 工作方式：
//   1) UniversalRenderer 在调试开关打开时，让 UI 相机的 Uber 不直接解析到屏幕
//      （resolveToScreen=false），这样 Uber 的输出（最终画面）留在相机颜色缓冲里；
//   2) 本 pass 在其后回读该缓冲，逐像素统计亮度均值/分位数并打日志。
//   3) DebugMode: 0=自动(现状) 1=强制关 Uber 的 sRGB 编码 2=强制开 —— 三种模式的数值差异
//      直接说明「Uber 的 sRGB 关键字在��平台是否真的生效」。
public static class ColorProbe
{
    /// <summary>调试总开关：打开后 UI 相机的 Uber 改为不直接解析到屏幕，改由 ColorProbePass 回读。</summary>
    public static bool Enabled;

    /// <summary>0=自动(现状) 1=强制关 Uber 的 sRGB 编码 2=强制开。</summary>
    public static int Mode;

    /// <summary>构建标识：探针改动后必须递增，日志里用它确认跑的是新包（避免测旧包的坑）。</summary>
    public const string BuildStamp = "PROBE-D6";

    /// <summary>像素统计（亮度均值/分位数）——屏幕抓取与 GPU 回读共用。</summary>
    public static void Analyze(string tag, Color32[] data, string extra = "")
    {
        if (data == null || data.Length == 0) { Debug.Log("[ColorProbe] " + tag + " empty"); return; }
        int n = data.Length;
        double sumR = 0, sumG = 0, sumB = 0;
        var lums = new float[n];
        for (int i = 0; i < n; i++)
        {
            var c = data[i];
            sumR += c.r; sumG += c.g; sumB += c.b;
            lums[i] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }
        System.Array.Sort(lums);
        double mean = 0; for (int i = 0; i < n; i++) mean += lums[i]; mean /= n;
        Debug.Log($"[ColorProbe] {BuildStamp} {tag}{extra} meanR={sumR / n:F1} meanG={sumG / n:F1} meanB={sumB / n:F1}"
            + $" lumaMean={mean:F2} p05={lums[n / 20]:F1} p50={lums[n / 2]:F1} p95={lums[(n * 19) / 20]:F1} max={lums[n - 1]:F1}");
    }
}

public class ColorProbePass : ScriptableRenderPass
{
    static int s_LastLoggedMode = -1;
    static int s_LastNullLogged = -1;
    static int s_LastExecLogged = -1;
    static readonly int s_ProbeId = Shader.PropertyToID("_ColorProbe");

    RTHandle m_Source;
    Texture m_Tex;   // 探针模式：相机 targetTexture（Uber 最终输出落在这里）
    string m_Tag;

    public ColorProbePass()
    {
        // Uber 在 BeforeRenderingPostProcessing 执行并直接解析到屏幕 ⇒ 相机颜色缓冲里仍是
        // 「Uber 的输入」；本 pass 在其后回读该缓冲，即可跨平台对比链内状态。
        renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing + 1;
    }

    public void Setup(RTHandle source, string tag)
    {
        m_Source = source;
        m_Tex = null;
        m_Tag = tag;
    }

    public void SetupTexture(Texture tex, string tag)
    {
        m_Source = null;
        m_Tex = tex;
        m_Tag = tag;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var rt = m_Tex != null ? m_Tex : m_Source?.rt;
        if (rt == null)
        {
            // 源缓冲无效时也要留痕（此前静默 return，导致完全看不到探针输出）
            if (ColorProbe.Mode != s_LastNullLogged)
            {
                s_LastNullLogged = ColorProbe.Mode;
                Debug.Log("[ColorProbe] " + ColorProbe.BuildStamp + " source rt is null (source=" + (m_Source == null ? "null" : m_Source.name) + ")");
            }
            return;
        }

        // 只在模式变化时输出一行（否则每帧都会刷屏）
        bool shouldLog = ColorProbe.Mode != s_LastLoggedMode;
        if (shouldLog) s_LastLoggedMode = ColorProbe.Mode;

        if (ColorProbe.Mode != s_LastExecLogged)
        {
            s_LastExecLogged = ColorProbe.Mode;
            Debug.Log("[ColorProbe] " + ColorProbe.BuildStamp + " pass executing, mode=" + ColorProbe.Mode + " rt=" + rt.width + "x" + rt.height);
        }

        var cmd = CommandBufferPool.Get("ColorProbe");
        cmd.RequestAsyncReadback(rt, 0, TextureFormat.RGBA32, (AsyncGPUReadbackRequest req) =>
        {
            if (req.hasError)
            {
                Debug.Log("[ColorProbe] " + m_Tag + " readback error");
                return;
            }
            var data = req.GetData<Color32>().ToArray(); // NativeArray -> 数组（调试用，允许拷贝）
            if (!shouldLog) return;
            ColorProbe.Analyze(m_Tag, data, " mode=" + ColorProbe.Mode + " src=RT " + rt.width + "x" + rt.height);
        });
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}
