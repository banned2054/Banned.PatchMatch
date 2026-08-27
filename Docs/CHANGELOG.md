# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Bench: `realcorpus` mode that freezes scalar CPU inpaint outputs for the real manga
  corpus (per-image PNGs, side-by-side comparisons, gallery, and a SHA-256 manifest
  serving as the byte-level regression reference). Uses SixLabors.ImageSharp 3.1.12 as
  a Bench-only development dependency for PNG/JPG decoding.

## [0.1.0] - 2026-08-24

### Added

- Pure managed port of PyPatchMatch's multi-scale inpainting algorithm.
- Bidirectional nearest-neighbor fields with propagation and random search.
- Gradient-aware patch SSD and regularity-guided distance metrics.
- Hole-mask, global-mask, inferred-white-mask, and destination-buffer overloads.
- Deterministic per-operation random state and cancellation support.
- .NET 8, .NET 9, .NET 10, and NativeAOT compatibility.
- NUnit behavior and validation tests plus a NativeAOT smoke application.
