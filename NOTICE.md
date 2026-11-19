# 声明与权利归属（NOTICE）

## 1. 非官方项目

本仓库是个人技术学习与作品集项目，与鹰角网络（Hypergryph）、BefourStudios 及其他资产的权利方没有任何关系，也未获得其授权、认可或赞助。

## 2. 画面中的角色与场景

- **角色**：截图中的角色是《明日方舟：终末地》的庄方宜，使用的是同人改造的 PMX 模型。角色及其设计的权利归**鹰角网络**所有。模型的使用规则要求借物署名"鹰角网络"、仅限非商业用途、不得声称是原作者。本仓库不分发该模型的任何文件（网格、贴图、材质）。
- **场景**：截图中的庭院场景来自第三方资产包 **Asian Dynasty Environment v2.0（BefourStudios）**。本仓库不包含该资产包的任何文件（网格、贴图、材质、场景）。仓库里的 Editor 脚本会按路径引用这些资产，但不附带它们。
- **Suntail Village**（另一个第三方环境包，项目里用作回归测试场景）同样不包含在内；`SuntailMaterialConverter`、`PbrCalibrationBoard` 和 `FinalImageSetup` 三个脚本按路径引用它。

`docs/images/` 中的截图和动图只用于展示本项目的渲染技术。其中角色和场景的部分不适用本仓库的 MIT 许可证，权利归原权利人。图片均已压缩，不能当作素材使用。

## 3. 移植和改编的代码

以下代码改编自第三方项目，**保留原许可，不适用 MIT**。每个文件开头写明了来源和改动，许可文本在 [`LICENSES/`](LICENSES)。

### Illusion Render Pipelines

[IllusionRP](https://github.com/AkiKurisu/IllusionRP)（`com.kurisu.illusion-render-pipelines`）以 Unity Companion License 发布，其 GTAO 和接触阴影实现源自 Unity HDRP（Copyright © Unity Technologies ApS，Unity Companion License）。许可文本：[`LICENSES/Unity-Companion-License.md`](LICENSES/Unity-Companion-License.md)，原项目的许可声明：[`LICENSES/Unity-Companion-License-notices.md`](LICENSES/Unity-Companion-License-notices.md)。

- `Assets/HybridRP/Shaders/Include/HybridGTAOCommon.hlsl`
- `Assets/HybridRP/Shaders/Include/HybridGTAOConstants.hlsl`（常量缓冲布局与原实现一致）
- `Assets/HybridRP/Shaders/Compute/HybridGTAO.compute`
- `Assets/HybridRP/Shaders/Compute/HybridGTAOBlurUpsample.compute`
- `Assets/HybridRP/Shaders/Compute/HybridDepthPyramid.compute`
- `Assets/HybridRP/Shaders/Compute/HybridContactShadows.compute`
- `Assets/HybridRP/Runtime/Passes/HybridGTAOPass.cs`（参数打包与图集布局部分）
- `Assets/HybridRP/Runtime/Passes/HybridContactShadowsPass.cs`（参数打包部分）
- `Assets/HybridRP/Runtime/HybridGTAO.cs`、`Assets/HybridRP/Runtime/HybridContactShadows.cs`（Volume 参数沿用原实现的设计）

IllusionRP 中带 GPL-3 头的屏幕空间阴影 resolve 没有使用；`HybridScreenSpaceShadows.shader` 是参照 URP 自带实现重新编写的。

### MiniEngine

Microsoft MiniEngine（MIT）：`HybridGTAOBlurUpsample.compute` 中的 smart blur 和双边上采样。原版权声明保留在该文件开头，也见 [`LICENSES/MiniEngine-MIT.txt`](LICENSES/MiniEngine-MIT.txt)。

### Endfield URP Lit Shader（TheOnlyProblem）

Copyright (c) 2025 TheOnlyProblem，MIT，许可文本见 [`LICENSES/TheOnlyProblem-Endfield-URP-Lit-Shader-MIT.txt`](LICENSES/TheOnlyProblem-Endfield-URP-Lit-Shader-MIT.txt)。

- `Assets/HybridRP/Shaders/Include/HybridCharacterCommon.hlsl` 中的 `HybridSDFThreshold`：SDF 角度阈值的计算改编自其 `SetSDFProperty.cs`，从 C# 移到了 shader 里。

屏幕空间深度 rim 的思路也参考了这个项目，代码是重新写的。

## 4. 参考但未复制代码的项目

- chris0214 / Arknights-Endfield-MME-Shader（MIT）
- Morgana-lgtm / Perlica Character Shader

## 5. Unity 与 URP

本项目基于 Unity 6000.0.83f1 和 Universal Render Pipeline 17.0.4（Unity Technologies，Unity Companion License）。着色器通过 `#include` 引用 URP 包，本仓库不包含 URP 的源码。`Assets/Settings/` 中的 URP 资产是本项目自己的配置文件。

## 6. 许可证范围

本仓库中**原创的代码、着色器、工具脚本、文档和示意图**以 MIT 发布（见 `LICENSE`）。以下内容不在 MIT 范围内：

- 第 3 节列出的移植和改编代码（保留各自的原许可）；
- 截图和动图中的角色、场景及其设计；
- 游戏名称、商标与标识。

## 7. 删除请求

如果你是相关内容的权利方，认为本仓库侵犯了你的权利，请通过仓库 Issues 联系作者。核实后会立即删除或修改相关内容。
