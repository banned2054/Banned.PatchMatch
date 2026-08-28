# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Pure managed port of PyPatchMatch's multi-scale inpainting algorithm.
- Bidirectional nearest-neighbor fields with propagation and random search.
- Gradient-aware patch SSD and regularity-guided distance metrics.
- Hole-mask, global-mask, inferred-white-mask, and destination-buffer overloads.
- Deterministic per-operation random state and cancellation support.
- Runtime-dispatched SIMD patch-distance kernels for AVX-512, AVX2, SSE, and AdvSimd,
  with a scalar fallback and byte-identical results across paths.
- .NET 8, .NET 9, .NET 10, and NativeAOT compatibility.
- NUnit behavior, validation, golden-baseline, and SIMD differential tests.
- Benchmark and corpus tooling for performance analysis and byte-level regression
  verification, kept separate from the NuGet package.
- NativeAOT smoke application.
