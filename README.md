# Banned.PatchMatch

English | [简体中文](Docs/README.md)

[![License](https://img.shields.io/badge/license-Apache_2.0-green)](./LICENSE)

**Banned.PatchMatch** is a pure managed .NET implementation of PatchMatch-based image inpainting. It ports the multi-scale, bidirectional nearest-neighbor-field and expectation-maximization workflow used by PyPatchMatch, with runtime CPU SIMD acceleration and no OpenCV, P/Invoke, or native runtime files.

> **Note**: The convergence behavior matches dmMaze's PyPatchMatchInpaint fork (the inpainting backend shipped with BallonsTranslator).

## Features

- **Pure Managed**: No native library dependencies of any kind.
- **SIMD Accelerated**: The patch-distance kernel dispatches to vector code at runtime (AVX-512 / AVX2 / SSE on x64, AdvSimd on ARM64) and falls back to scalar code automatically. All paths produce byte-identical output.
- **Cross-Platform**: Targets .NET 8, .NET 9, and .NET 10; compatible with NativeAOT.
- **Flexible Input**: Tightly packed RGB/BGR byte buffers, hole masks, global exclusion masks, and regularity guide maps.
- **Deterministic**: Per-operation random state with fixed-seed stability, plus cancellation support.
- **Allocation-Friendly**: Overloads that write into caller-owned buffers.

## Installation

```bash
dotnet add package Banned.PatchMatch
```

## Usage

### Basic Usage

The image must contain exactly `width * height * 3` bytes; the mask must contain exactly `width * height` bytes. Zero keeps a pixel and any non-zero value marks a hole.

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

`PatchRadius` follows PyPatchMatch's `patch_size` semantics: a radius of `3` compares `7 x 7` patches.

### Writing Into an Existing Buffer

```csharp
PatchMatchInpainter.Inpaint(
    image,
    mask,
    width,
    height,
    destination,
    new PatchMatchOptions { PatchRadius = 3 });
```

### White-Pixel Mask Inference

The overload without a mask treats pixels whose three channels are all `255` as holes:

```csharp
byte[] result = PatchMatchInpainter.Inpaint(image, width, height);
```

### Global Exclusion Mask

A global mask excludes pixels from nearest-neighbor matching and voting:

```csharp
byte[] result = PatchMatchInpainter.Inpaint(
    image,
    mask,
    globalMask,
    width,
    height);
```

Global-mask pixels are excluded algorithmic regions, not protected source pixels; their final values are not guaranteed to equal the input after pyramid scaling.

### Regularity-Guided Inpainting

The guide map provides two `float` values per pixel holding normalized periodic coordinates (a third channel is accepted for PyPatchMatch compatibility and ignored):

```csharp
byte[] result = PatchMatchInpainter.InpaintRegularity(
    image,
    mask,
    guideMap,
    width,
    height,
    guideWeight: 0.25f);
```

## Compatibility Notes

- Channel order is preserved; the library does not convert between RGB and BGR.
- Buffers must be tightly packed; row-stride conversion belongs at the image-framework boundary.
- A fixed seed produces stable output within this implementation, and results match dmMaze's PyPatchMatchInpaint DLL to rounding-level differences. Byte-for-byte equality with every native build is not a public guarantee (`rand()`, floating-point precision, and compiler optimizations vary).
- PatchMatch is CPU- and memory-intensive. Cropping work to the relevant repair region is preferable for large images.

## Project Structure

| Project | Responsibility |
| --- | --- |
| `Banned.PatchMatch` | Public buffer API and pure managed PatchMatch implementation. |
| `Banned.PatchMatch.Test` | Deterministic behavior, validation, guide-map, and SIMD differential tests. |
| `Banned.PatchMatch.Bench` | Development-only benchmark and corpus tooling (not shipped in the package). |
| `Banned.PatchMatch.AotSmoke` | NativeAOT compatibility smoke application. |

## License

Copyright (c) 2026 banned.

Licensed under the [Apache License 2.0](LICENSE). The implementation is derived from the MIT-licensed PyPatchMatch project; see [NOTICE](NOTICE) for upstream attribution and license terms.

## Contribution

Issues and Pull Requests are welcome!

## Support

If you encounter any issues while using this library, please open an Issue on GitHub.
