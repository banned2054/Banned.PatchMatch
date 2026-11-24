using Vortice.Direct3D12;

namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 使用成对 uint32 原子槽保存四个 uint64 定点累加值的阶段 2 GPU 投票缓冲。<br/>
/// Phase-2 GPU vote buffer storing four uint64 fixed-point accumulators as pairs of uint32 atomic lanes.
/// </summary>
internal sealed class Direct3D12FixedPointVoteBuffer : IDisposable
{
    internal const uint WeightScale = 65_535;
    internal const int BytesPerPixel = sizeof(uint) * 8;

    private readonly Direct3D12Prototype _owner;
    private int _disposed;

    internal Direct3D12FixedPointVoteBuffer(Direct3D12Prototype owner, Direct3D12PatchWorkset workset,
                                             ID3D12Resource buffer)
    {
        _owner = owner;
        Workset = workset;
        Buffer = buffer;
    }

    internal Direct3D12PatchWorkset Workset { get; }

    internal ID3D12Resource Buffer { get; }

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
        _owner.ReleaseFixedPointVoteBuffer(this);
    }
}
