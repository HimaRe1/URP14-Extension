# URP-14-Extension-Fixed

基于 [killop/URP-12-Extension](https://github.com/killop/URP-12-Extension) 的 **URP 14 迁移修复版**（前作：[URP-12-Extension-Fixed](https://github.com/HimaRe1/URP12-Extension-Fixed)）。

**环境**：URP 14.0.12 嵌入式包 / Unity 2022.3.x（开发验证于 2022.3.62f2，Linear 色彩空间）

## 这是什么

将 URP-12-Extension 的「场景/UI 分辨率分离 + swap buffer + Gamma 矫正」魔改管线完整迁移到 URP 14，并移植/修复过程中的BUG。

三大核心特性（与 URP12 版一致）：

- **场景/UI 分辨率分离**（`sUISplitEnable`）：场景按 renderScale 降分辨率渲染，UI 保持全分辨率
- **UI 相机独立 swap buffer 合成**（`sEnableUICameraUseSwapBuffer`）
- **Gamma 矫正框架**（`sIsGammaCorrectEnable`）：UI Shader 经 `_IsInUICamera` 在 Gamma 空间混合

另包含：

- **77 个后处理效果**（X-PostProcessing-URP 全量迁移闭合：ColorAdjustment / Pixelize / Vignette / Sharpen / Glitch / EdgeDetection / Blur 家族 / Environment 家族，RTHandle 适配）
- **SceneLight**（Volume 组件覆盖全局 RenderSettings：三色环境光 / Cubemap 反射 / 雾，含反射源回退与 Lighting 面板不被覆盖修复）

## 目录结构与安装

| 目录 / 文件                                           | 放到工程的哪里                                                | 说明                                                                   |
| ------------------------------------------------- | ------------------------------------------------------ | -------------------------------------------------------------------- |
| `com.unity.render-pipelines.universal@14.0.12/`   | `Packages/`                                            | **必需**                                                               |
| `com.happyelements.adaptiveperformance/`          | `Packages/`                                            | **必需**                                                               |
| `timeline-volume/`                                | `Assets/`                                              | Timeline 驱动 Volume 的轨道（4 个脚本），可选，依赖 `com.unity.timeline`             |
| `shaders/URP-UI-Default.shader` + `UI_Pixel.hlsl` | `Assets/` 下任意目录                                        | **两个文件必须放在同一目录**，建议手动替换项目的默认 UI 材质                                   |
| `shaders/TMP_SDF-Mobile.shader`                   | 覆盖 `Assets/TextMesh Pro/Shaders/TMP_SDF-Mobile.shader` | ⚠️ **只覆盖 .shader 文件，保留你工程里原有的 .meta**（meta 的 GUID 被 TMP 默认材质引用，换了会断） |

**PS．manifest.json 里的 URP 条目要删除，否则会被官方版本覆盖，导入包无用**

## 使用

三个核心开关（静态变量，可用代码 / 游戏内面板设置）：

```
UniversalRenderPipeline.sUISplitEnable               // 场景降分辨率、UI 全分辨率
UniversalRenderPipeline.sIsGammaCorrectEnable        // UI Gamma 矫正（依赖 swap buffer）
UniversalRenderPipeline.sEnableUICameraUseSwapBuffer // UI 相机走 swap buffer
```

- **推荐形态**：split 开 + swap 开 + gamma 开；**UI 相机后处理开**，且 UI 相机的 **Volume Layer Mask 隔离 XPP 效果层**（后处理整栈只跑一次、UI 不被 XPP 二次处理的关键）
- **NN / Bilinear 上采样**：split 开时在 `BlitPass` 内生效（按 `upscalingFilter` 自动选择采样 pass）；**FSR 未支持**（退化为 Bilinear）
- **FXAA**：swap 开时前置到基底相机执行（作用于场景、UI 绘制之前），swap 关时走原生栈尾
- **SMAA**：走相机后处理链（需 Base 相机后处理开），作用于低分辨率场景缓冲
- **Gamma 矫正**只在 swap 开时执行

## 修改清单（相对 killop 原版 / URP12 版）

**1. URP 14 全量迁移**：77 个 XPP 效果迁移闭合（RTHandle 体系适配、Blur 家族固定尺寸金字塔、深度纹理链内可用、`_ScaledScreenParams` 对齐链工作缓冲分辨率）；修复原版 5 处功能缺陷（Kawase 参数绑定 / DualKawase 上采样 pass / TiltShift+IrisBlur 遮罩 / RaderWave shader 名拼写 / 同 CommandBuffer 材质末值覆盖）。

**2. Gamma 矫正链修复**：补齐 **Line2Gamma 去程**（Upscale 扩容 blit 的 Linear→SRGB 编码，与 Gamma2Line 回程构成完整往返，缺去程会导致场景二次解码变暗）；`FinalBlit` 的 isLine 极性对齐 `uiBufferBackToLinear` 语义 + 非 swap 路径补 SRGBToLinear 解码分支（选错方向即泛白/偏暗）。

**3. renderScale 总闸一致性**：`sUISplitEnable` 关闭时同步关闭 D1-a 全部扩容/覆写机器，消除「总闸关但栈尾落入原生 FinalPost 直出」导致的黑屏中间态。

**4. 上采样接线**：`BlitPass` 按 `upscalingFilter` 选择采样 pass，NN 与 Bilinear 在 split 开时都生效。

**5. FXAA 修复**：swap 开时 FXAA 前置到基底相机（`useRenderColorBuff` 写回 swap buffer），对颜色链完全透明。

**6. SceneLight**：RenderSettings 覆盖器（三色环境光 / Cubemap 反射 / 雾），修复 cubemap 为 null 时全场反射全黑、Lighting 面板恢复可用、删除零消费者死参数（`my*` 系列）。

## 已知约束

- MSAA 必须保持 Disabled（URP Asset `m_MSAA: 1`）
- 非 swap buffer 模式下 Gamma 矫正不可用（实现依赖 swap buffer 合成路径）
- **FSR 未支持**（源版即未放行，选项会退化为 Bilinear）
- 运行时切换 Gamma 矫正会有一帧黑屏（管线状态瞬态，无累积影响）
- 渲染到 RT / XR 未适配
- TMP 仅覆盖 `TMP_SDF-Mobile` 一个 shader；Overlay / SSD 等 variant 未接 Gamma 链路

## 致谢

- [killop/URP-12-Extension](https://github.com/killop/URP-12-Extension) —— 本项目的基底
- [URP-12-GammaUIAndSplitResolution](https://github.com/killop/URP-12-GammaUIAndSplitResolution) —— Gamma UI / 分辨率分离思路来源
- [X-PostProcessing-URP](https://github.com/tkonexhh/X-PostProcessing-URP) —— 原版集成的 70+ 后处理效果
