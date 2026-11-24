using System.Runtime.InteropServices;

namespace Banned.PatchMatch.Direct3D12;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PatchPair(int SourceY, int SourceX, int TargetY, int TargetX);
