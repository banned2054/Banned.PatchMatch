using Vortice.Direct3D12;

namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 驻留在 GPU 默认堆中的阶段 2 图像工作集。<br/>
/// Phase-2 image workset resident in GPU default heaps.
/// </summary>
internal sealed class Direct3D12PatchWorkset : IDisposable
{
    private readonly Direct3D12Prototype _owner;
    private int _disposed;

    internal Direct3D12PatchWorkset(
        Direct3D12Prototype owner, int width, int height,
        ID3D12Resource sourcePixels, ID3D12Resource sourceMask, ID3D12Resource sourceInvalid,
        ID3D12Resource sourceGlobal,
        ID3D12Resource targetPixels, ID3D12Resource targetInvalid, ID3D12Resource targetGlobal)
    {
        _owner        = owner;
        Width         = width;
        Height        = height;
        SourcePixels  = sourcePixels;
        SourceMask    = sourceMask;
        SourceInvalid = sourceInvalid;
        SourceGlobal  = sourceGlobal;
        TargetPixels  = targetPixels;
        TargetInvalid = targetInvalid;
        TargetGlobal  = targetGlobal;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal ID3D12Resource SourcePixels { get; }

    internal ID3D12Resource SourceMask { get; }

    internal ID3D12Resource SourceInvalid { get; }

    internal ID3D12Resource SourceGlobal { get; }

    internal ID3D12Resource TargetPixels { get; }

    internal ID3D12Resource TargetInvalid { get; }

    internal ID3D12Resource TargetGlobal { get; }

    internal Direct3D12Prototype Owner => _owner;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TargetGlobal.Dispose();
        TargetInvalid.Dispose();
        TargetPixels.Dispose();
        SourceGlobal.Dispose();
        SourceInvalid.Dispose();
        SourceMask.Dispose();
        SourcePixels.Dispose();
    }

    public void Dispose()
    {
        _owner.ReleasePatchWorkset(this);
    }
}

/// <summary>
/// 工作集 7 个缓冲的回读快照，仅供阶段 2 原型差分验证使用。<br/>
/// Readback snapshot of all seven workset buffers, for phase-2 prototype differential validation only.
/// </summary>
internal readonly record struct PatchWorksetSnapshot(
    byte[] SourcePixels, byte[] SourceMask, byte[] SourceInvalid, byte[] SourceGlobal,
    byte[] TargetPixels, byte[] TargetInvalid, byte[] TargetGlobal);
