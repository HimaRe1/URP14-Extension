using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// *URP14Port-B07FIX-XppAlwaysIncluded：XPP 效果 shader 只靠 AbstractVolumeRenderer 的
// Shader.Find 按名取用、无任何资产引用 ⇒ 打包被 Unity 裁剪（Editor 正常/真机全灭）。
// 本工具把全部效果 shader 写入 Project Settings > Graphics > Always Included Shaders。
// 通过 SerializedObject 在编辑器内修改（直接改 ProjectSettings 文件会被打开中的编辑器覆盖回）。
// 用法：菜单 Tools > XPP > 将效果 Shader 加入 Always Included Shaders，构建前执行一次即可。
// 扫描方式：全工程 t:Shader + 路径过滤（不依赖包文件夹名/@版本后缀，任何工程结构都能用）。
public static class XppAlwaysIncludedTool
{
    const string k_PathMark = "X-PostProcessing-URP/Volume/Effect/";

    [MenuItem("Tools/XPP/将效果 Shader 加入 Always Included Shaders")]
    public static void AddAll()
    {
        var shaders = new List<Shader>();
        int scanned = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
            if (!path.Contains(k_PathMark))
                continue;
            scanned++;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
                shaders.Add(shader);
        }
        Debug.Log($"[XppAlwaysIncluded] 全工程扫描到 XPP 效果 shader {scanned} 个（路径含 {k_PathMark}）");
        if (scanned == 0)
        {
            Debug.LogError("[XppAlwaysIncluded] 一个都没扫到——当前编辑器工程里没有 X-PostProcessing-URP 效果 shader，请确认打开的是正确工程。");
            return;
        }

        var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (settings == null || settings.Length == 0)
        {
            Debug.LogError("[XppAlwaysIncluded] 找不到 GraphicsSettings");
            return;
        }
        var so = new SerializedObject(settings[0]);
        var list = so.FindProperty("m_AlwaysIncludedShaders");

        int added = 0, existed = 0;
        foreach (var shader in shaders)
        {
            bool found = false;
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                { found = true; break; }
            if (found) { existed++; continue; }
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            added++;
        }
        so.ApplyModifiedProperties();
        Debug.Log($"[XppAlwaysIncluded] 效果 shader 共 {shaders.Count}：新增 {added}，已存在 {existed}。构建前无需重复执行（已存在则跳过）。");
    }
}
