# Banned.PatchMatch

[English](../README.md) | 简体中文

[![License](https://img.shields.io/badge/license-Apache_2.0-green)](../LICENSE)

**Banned.PatchMatch** 是 PatchMatch 图像修复算法的纯托管 .NET 实现。它移植了 PyPatchMatch
使用的多尺度金字塔、双向最近邻场和期望最大化投票流程，运行时自动启用 CPU SIMD 加速，
不依赖 OpenCV、P/Invoke 或任何原生 DLL。

> **注**：收敛行为与 dmMaze 的 PyPatchMatchInpaint 分支（BallonsTranslator 内置的修复后端）保持一致。

## 功能

- **纯托管**：不依赖任何原生库。
- **SIMD 加速**：补丁距离内核运行时自动分派到向量代码（x64 上的 AVX-512 / AVX2 / SSE，ARM64 上的 AdvSimd），无对应指令集时自动回退标量实现；所有路径输出逐字节一致。
- **跨平台**：支持 .NET 8、.NET 9 和 .NET 10，兼容 NativeAOT。
- **灵活输入**：连续排列的 RGB/BGR 字节缓冲区、孔洞蒙版、全局排除蒙版和规律性引导图。
- **确定性**：每次调用使用独立随机状态，固定种子输出稳定，支持取消。
- **低分配**：提供写入调用方缓冲区的重载。

## 安装

```bash
dotnet add package Banned.PatchMatch
```

## 用法

### 基本用法

图像长度必须恰好为 `width * height * 3`，蒙版长度必须恰好为 `width * height`。
蒙版中的 `0` 表示保留，任意非零值表示需要修复。

```csharp
using Banned.PatchMatch;

byte[] result = PatchMatchInpainter.Inpaint(
    image,
    mask,
    width,
    height,
    new PatchMatchOptions
    {
        PatchRadius = 3,
        RandomSeed = 1212
    });
```

`PatchRadius` 对应 PyPatchMatch 的 `patch_size` 实际语义：半径为 `3` 时比较的是 `7 x 7` 补丁。

### 写入已有缓冲区

```csharp
PatchMatchInpainter.Inpaint(
    image,
    mask,
    width,
    height,
    destination,
    new PatchMatchOptions { PatchRadius = 3 });
```

### 自动识别白色孔洞

不传蒙版的重载会把三个通道均为 `255` 的纯白像素视为孔洞：

```csharp
byte[] result = PatchMatchInpainter.Inpaint(image, width, height);
```

### 全局排除蒙版

全局蒙版会把相应像素排除在最近邻匹配和投票之外：

```csharp
byte[] result = PatchMatchInpainter.Inpaint(
    image,
    mask,
    globalMask,
    width,
    height);
```

全局蒙版表示算法排除区域，不是“保护原像素不变”的蒙版；经过金字塔缩放后，
这些位置的最终值不保证等于输入原值。

### 规律性引导修复

引导图每个像素提供两个 `float` 归一化周期坐标（为了兼容 PyPatchMatch 可以传入第三通道，
但算法不会读取它）：

```csharp
byte[] result = PatchMatchInpainter.InpaintRegularity(
    image,
    mask,
    guideMap,
    width,
    height,
    guideWeight: 0.25f);
```

## 兼容性说明

- 算法保持输入通道顺序，不负责 RGB 与 BGR 转换。
- 输入必须连续排列；带行跨度的图像应在图像框架边界处转换。
- 固定随机种子时，本托管实现自身的输出保持稳定，且与 dmMaze 的 PyPatchMatchInpaint DLL
  仅存在舍入级差异。不同原生编译器的 `rand()`、浮点精度和优化方式不同，因此不把与所有
  原生版本逐字节相等作为公开兼容承诺。
- PatchMatch 对 CPU 和内存开销较大。处理大图时建议先裁剪到实际需要修复的区域。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `Banned.PatchMatch` | 缓冲区公开 API 与纯托管 PatchMatch 算法。 |
| `Banned.PatchMatch.Test` | 确定性、输入校验、引导图与 SIMD 差分测试。 |
| `Banned.PatchMatch.Bench` | 仅开发使用的基准与语料工具（不进入 NuGet 包）。 |
| `Banned.PatchMatch.AotSmoke` | NativeAOT 兼容性冒烟程序。 |

## 许可证

Copyright (c) 2026 banned。

本项目使用 [Apache License 2.0](../LICENSE)。实现派生自采用 MIT 许可证的 PyPatchMatch；
上游归属和许可条款参见 [NOTICE](../NOTICE)。

## 贡献

欢迎提交 Issue 和 Pull Request！

## 支持

使用过程中遇到任何问题，请在 GitHub 上提交 Issue。
