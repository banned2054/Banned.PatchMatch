# Banned.PatchMatch

English | [简体中文](Docs/README.md)

**Banned.PatchMatch** is a pure managed .NET implementation of PatchMatch-based image inpainting.
It ports the multi-scale, bidirectional nearest-neighbor-field and expectation-maximization workflow
used by PyPatchMatch without OpenCV, P/Invoke, or native runtime files. The convergence behavior
matches dmMaze's PyPatchMatchInpaint fork (the inpainting backend shipped with BallonsTranslator):
the source-to-target field only minimizes and votes for hole pixels, and the expectation-maximization
loop terminates early once both nearest-neighbor fields stop improving.

## Features

- Pure managed code with no native library dependencies.
- Targets .NET 8, .NET 9, and .NET 10.
- Compatible with NativeAOT.
- Accepts tightly packed three-channel RGB or BGR byte buffers.
- Supports hole masks, global exclusion masks, and regularity guide maps.
- Uses deterministic per-operation random state and supports cancellation.
- Provides allocation-friendly overloads that write into caller-owned buffers.

## Installation

```bash
dotnet add package Banned.PatchMatch
```

## Basic usage

The image must contain exactly `width * height * 3` bytes. The mask must contain exactly
`width * height` bytes; zero keeps a pixel and any non-zero value marks a hole.

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

`PatchRadius` follows PyPatchMatch's `patch_size` behavior. A radius of `3` compares `7 x 7`
patches.

To write into an existing buffer:

```csharp
PatchMatchInpainter.Inpaint(
    image,
    mask,
    width,
    height,
    destination,
    new PatchMatchOptions { PatchRadius = 3 });
```

## White-pixel mask inference

An overload without a mask treats pixels whose three channels are all `255` as holes:

```csharp
byte[] result = PatchMatchInpainter.Inpaint(image, width, height);
```

## Global exclusion mask

A global mask excludes pixels from nearest-neighbor matching and voting:

```csharp
byte[] result = PatchMatchInpainter.Inpaint(
    image,
    mask,
    globalMask,
    width,
    height);
```

Global-mask pixels are excluded algorithmic regions, not protected source pixels. Their final
values are not guaranteed to equal the original input after pyramid scaling.

## Regularity-guided inpainting

The regularity overload accepts two or three `float` values per pixel. The first two channels are
normalized periodic coordinates; a third channel is accepted for PyPatchMatch compatibility and
ignored.

```csharp
byte[] result = PatchMatchInpainter.InpaintRegularity(
    image,
    mask,
    guideMap,
    width,
    height,
    guideWeight: 0.25f);
```

## Compatibility notes

- Channel order is preserved. The library does not convert between RGB and BGR.
- Buffers must be tightly packed; row-stride conversion belongs at the image-framework boundary.
- A fixed seed produces stable output within this managed implementation.
- The convergence strategy matches dmMaze's PyPatchMatchInpaint DLL used by BallonsTranslator;
  on identical inputs the output is near-identical, with only rounding-level differences caused by
  native compiler floating-point settings.
- The algorithm follows PyPatchMatch, but byte-for-byte equality with every native build is not a
  public guarantee because C `rand()`, floating-point precision, and compiler optimizations vary.
- PatchMatch is CPU- and memory-intensive. Cropping work to the relevant repair region is preferable
  for large images.

## Project structure

| Project | Responsibility |
| --- | --- |
| `Banned.PatchMatch` | Public buffer API and pure managed PatchMatch implementation. |
| `Banned.PatchMatch.Test` | Deterministic behavior, mask, validation, and guide-map tests. |
| `Banned.PatchMatch.AotSmoke` | NativeAOT compatibility smoke application. |

## License

Copyright (c) 2026 banned.

Licensed under the [Apache License 2.0](LICENSE). The implementation is derived from the MIT-licensed
PyPatchMatch project; see [NOTICE](NOTICE) for upstream attribution and license terms.
