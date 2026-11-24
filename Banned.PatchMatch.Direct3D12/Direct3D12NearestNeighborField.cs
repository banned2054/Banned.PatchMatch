using Vortice.Direct3D12;

namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 阶段 2 的 GPU 最近邻场。第三个 uint 保存未归一化的 SSD 惩罚和，便于后续 shader 直接比较。<br/>
/// Phase-2 GPU nearest-neighbor field. Its third uint stores the unnormalized SSD penalty sum so
/// later shaders can compare candidates without 64-bit division.
/// </summary>
internal sealed class Direct3D12NearestNeighborField : IDisposable
{
    private readonly Direct3D12Prototype _owner;
    private int _disposed;

    internal Direct3D12NearestNeighborField(Direct3D12Prototype owner, Direct3D12PatchWorkset workset,
                                             ID3D12Resource buffer, int patchRadius, bool sourceToTarget)
    {
        _owner         = owner;
        Workset        = workset;
        Buffer         = buffer;
        PatchRadius    = patchRadius;
        SourceToTarget = sourceToTarget;
    }

    internal Direct3D12PatchWorkset Workset { get; }

    internal ID3D12Resource Buffer { get; }

    internal int PatchRadius { get; }

    internal bool SourceToTarget { get; }

    internal Direct3D12Prototype Owner => _owner;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Workset.ThrowIfDisposed();
    }

    internal void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Buffer.Dispose();
        }
    }

    public void Dispose()
    {
        _owner.ReleaseNearestNeighborField(this);
    }
}
