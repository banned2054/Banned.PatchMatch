using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 阶段 2 的内部 Direct3D 12 计算原型。该类型不是公共后端契约。<br/>
/// Internal Direct3D 12 compute prototype for phase 2. This type is not a public backend contract.
/// </summary>
internal sealed class Direct3D12Prototype : IDisposable
{
    private const int ShaderThreadCount = 64;
    private const int MaxDispatchGroupsPerDimension = 65_535;
    private const int DistanceScale = 65_535;
    private const int SsdScale = 9 * 255 * 255;
    private const int MaximumPatchRadius = 42;
    private const int MaximumInitializationAttempts = 20;

    private readonly SemaphoreSlim              _gate = new(1, 1);
    private readonly object                     _disposeSync = new();
    private readonly ID3D12Device                _device;
    private readonly ID3D12CommandQueue          _commandQueue;
    private readonly ID3D12RootSignature         _squareDifferenceRootSignature;
    private readonly ID3D12PipelineState         _squareDifferencePipelineState;
    private readonly ID3D12RootSignature         _patchDistanceRootSignature;
    private readonly ID3D12PipelineState         _patchDistancePipelineState;
    private readonly ID3D12RootSignature         _nearestNeighborInitializationRootSignature;
    private readonly ID3D12PipelineState         _nearestNeighborInitializationPipelineState;
    private readonly ID3D12RootSignature         _nearestNeighborMinimizationRootSignature;
    private readonly ID3D12PipelineState         _nearestNeighborMinimizationPipelineState;
    private readonly ID3D12RootSignature         _fixedPointVoteRootSignature;
    private readonly ID3D12PipelineState         _fixedPointVotePipelineState;
    private readonly ID3D12RootSignature         _setIdentityLinksRootSignature;
    private readonly ID3D12PipelineState         _setIdentityLinksPipelineState;
    private readonly ID3D12RootSignature         _fixedPointMaximizeRootSignature;
    private readonly ID3D12PipelineState         _fixedPointMaximizePipelineState;
    private readonly ID3D12RootSignature         _unpackRgbRootSignature;
    private readonly ID3D12PipelineState         _unpackRgbPipelineState;
    private readonly ID3D12RootSignature         _downsampleRootSignature;
    private readonly ID3D12PipelineState         _downsamplePipelineState;
    private readonly ID3D12RootSignature         _upsampleTargetRootSignature;
    private readonly ID3D12PipelineState         _upsampleTargetPipelineState;
    private readonly ID3D12Resource              _similarityWeightsBuffer;
    private readonly ID3D12Fence                 _fence;
    private          ulong                       _nextFenceValue = 1;
    private          int                         _asynchronousFenceWaitCount;
    private          bool                        _disposed;

    private Direct3D12Prototype(bool useWarp)
    {
        using var factory = CreateDXGIFactory2<IDXGIFactory6>(false);
        using var adapter = SelectAdapter(factory, useWarp);

        AdapterName = adapter.Description1.Description;
        IsWarp      = useWarp;
        _device     = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);

        var queueDescription = new CommandQueueDescription
        {
            Type  = CommandListType.Compute,
            Flags = CommandQueueFlags.None
        };
        _commandQueue = _device.CreateCommandQueue(queueDescription);

        var squareDifferenceShader = LoadShader("SquareDifference.dxil");
        _squareDifferenceRootSignature = _device.CreateRootSignature(squareDifferenceShader);
        _squareDifferencePipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _squareDifferenceRootSignature,
            ComputeShader = squareDifferenceShader
        });

        var patchDistanceShader = LoadShader("PatchDistance.dxil");
        _patchDistanceRootSignature = _device.CreateRootSignature(patchDistanceShader);
        _patchDistancePipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _patchDistanceRootSignature,
            ComputeShader = patchDistanceShader
        });

        var nearestNeighborInitializationShader = LoadShader("NearestNeighborInitialization.dxil");
        _nearestNeighborInitializationRootSignature =
            _device.CreateRootSignature(nearestNeighborInitializationShader);
        _nearestNeighborInitializationPipelineState =
            _device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _nearestNeighborInitializationRootSignature,
                ComputeShader = nearestNeighborInitializationShader
            });

        var nearestNeighborMinimizationShader = LoadShader("NearestNeighborMinimization.dxil");
        _nearestNeighborMinimizationRootSignature =
            _device.CreateRootSignature(nearestNeighborMinimizationShader);
        _nearestNeighborMinimizationPipelineState =
            _device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _nearestNeighborMinimizationRootSignature,
                ComputeShader = nearestNeighborMinimizationShader
            });

        var fixedPointVoteShader = LoadShader("FixedPointVote.dxil");
        _fixedPointVoteRootSignature = _device.CreateRootSignature(fixedPointVoteShader);
        _fixedPointVotePipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _fixedPointVoteRootSignature,
            ComputeShader = fixedPointVoteShader
        });

        var setIdentityLinksShader = LoadShader("SetIdentityLinks.dxil");
        _setIdentityLinksRootSignature = _device.CreateRootSignature(setIdentityLinksShader);
        _setIdentityLinksPipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _setIdentityLinksRootSignature,
            ComputeShader = setIdentityLinksShader
        });

        var fixedPointMaximizeShader = LoadShader("FixedPointMaximize.dxil");
        _fixedPointMaximizeRootSignature = _device.CreateRootSignature(fixedPointMaximizeShader);
        _fixedPointMaximizePipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _fixedPointMaximizeRootSignature,
            ComputeShader = fixedPointMaximizeShader
        });

        var unpackRgbShader = LoadShader("UnpackRgb.dxil");
        _unpackRgbRootSignature = _device.CreateRootSignature(unpackRgbShader);
        _unpackRgbPipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _unpackRgbRootSignature,
            ComputeShader = unpackRgbShader
        });

        var downsampleShader = LoadShader("Downsample.dxil");
        _downsampleRootSignature = _device.CreateRootSignature(downsampleShader);
        _downsamplePipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _downsampleRootSignature,
            ComputeShader = downsampleShader
        });

        var upsampleTargetShader = LoadShader("UpsampleTarget.dxil");
        _upsampleTargetRootSignature = _device.CreateRootSignature(upsampleTargetShader);
        _upsampleTargetPipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _upsampleTargetRootSignature,
            ComputeShader = upsampleTargetShader
        });

        var similarityWeights = BuildSimilarityWeights();
        _similarityWeightsBuffer = CreateUploadBuffer(
            MemoryMarshal.AsBytes(similarityWeights.AsSpan()),
            checked((ulong)similarityWeights.Length * sizeof(uint)));
        _fence = _device.CreateFence(0, FenceFlags.None);
    }

    internal string AdapterName { get; }

    internal bool IsWarp { get; }

    internal int AsynchronousFenceWaitCount => Volatile.Read(ref _asynchronousFenceWaitCount);

    internal static bool TryCreate(bool useWarp, [NotNullWhen(true)] out Direct3D12Prototype? prototype,
                                   out string diagnostic)
    {
        if (!OperatingSystem.IsWindows())
        {
            prototype  = null;
            diagnostic = "Direct3D 12 is only available on Windows.";
            return false;
        }

        try
        {
            prototype  = new Direct3D12Prototype(useWarp);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            prototype  = null;
            diagnostic = exception.ToString();
            return false;
        }
    }

    /// <summary>
    /// 在 GPU 上逐字节计算平方差，作为 PatchMatch SSD 距离路径的第一条计算切片。<br/>
    /// Computes per-byte squared differences on the GPU as the first compute slice of the PatchMatch SSD path.
    /// </summary>
    internal async Task<uint[]> SquareDifferencesAsync(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right,
                                                       CancellationToken cancellationToken = default)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The input buffers must have the same length.", nameof(right));
        }

        if (left.IsEmpty)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteSquareDifferencesAsync(left, right, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量计算候选 patch 的图像 SSD 距离。invalid 缓冲已经合并 hole mask 与 global mask。<br/>
    /// Calculates image SSD distances for a batch of patch pairs. The invalid buffers already merge
    /// the hole and global masks.
    /// </summary>
    internal async Task<int[]> CalculatePatchDistancesAsync(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceInvalid,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetInvalid,
        int width, int height, ReadOnlyMemory<PatchPair> pairs, int patchRadius,
        CancellationToken cancellationToken = default)
    {
        ValidatePatchDistanceInputs(sourcePixels, sourceInvalid, targetPixels, targetInvalid, width, height,
                                    patchRadius);
        if (pairs.IsEmpty)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecutePatchDistancesAsync(sourcePixels, sourceInvalid, targetPixels, targetInvalid,
                                                    width, height, pairs, patchRadius, cancellationToken)
               .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 一次上传两张图像和蒙版，创建可供多个 GPU pass 复用的默认堆工作集。<br/>
    /// Uploads two images and their masks once into a default-heap workset reusable by multiple GPU passes.
    /// </summary>
    internal async Task<Direct3D12PatchWorkset> CreatePatchWorksetAsync(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceMask,
        ReadOnlyMemory<byte> sourceGlobalMask,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetMask,
        ReadOnlyMemory<byte> targetGlobalMask,
        int width, int height, CancellationToken cancellationToken = default)
    {
        var pixelCount = ValidatePatchWorksetInputs(sourcePixels, sourceMask, sourceGlobalMask,
                                                     targetPixels, targetMask, targetGlobalMask,
                                                     width, height);
        var sourceGlobal  = ExpandGlobalMask(sourceGlobalMask, pixelCount);
        var targetGlobal  = ExpandGlobalMask(targetGlobalMask, pixelCount);
        var sourceInvalid = MergeInvalidMasks(sourceMask, sourceGlobal);
        var targetInvalid = MergeInvalidMasks(targetMask, targetGlobal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteCreatePatchWorksetAsync(sourcePixels, sourceMask, sourceInvalid, sourceGlobal,
                                                        targetPixels, targetInvalid, targetGlobal,
                                                        width, height, cancellationToken)
               .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把粗层工作集下采样一半，生成金字塔下一层的工作集。新工作集的 target 取"下采样后的 source 加空孔洞蒙版"。<br/>
    /// Downsamples a coarse workset by half into the next pyramid level's workset. The new target is the
    /// downsampled source with an empty hole mask.
    /// </summary>
    internal async Task<Direct3D12PatchWorkset> CreateDownsampledPatchWorksetAsync(
        Direct3D12PatchWorkset coarse, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(coarse);
        if (coarse.Width < 2 || coarse.Height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(coarse), $"({coarse.Width}, {coarse.Height})",
                                                  "The coarse workset must be at least 2x2 pixels.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            coarse.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteCreateDownsampledPatchWorksetAsync(coarse, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把粗层 target 按最近邻上采样到相邻细层，原地改写细层的 target 半边：粗层对应像素是孔洞或被全局排除时细层像素
    /// 保持 0，否则复制粗层 target 像素；invalid 是这些粗层导出的孔洞并上细层 source 的 global，global 整体换成
    /// 细层 source 的 global。<br/>
    /// Nearest-neighbor upsamples the coarse target into the adjacent fine level, rewriting the fine target
    /// half in place: pixels whose coarse pixel is a hole or globally excluded keep 0 and all others copy the
    /// coarse target pixel; invalid merges those coarse-derived holes with the fine source's global mask, and
    /// the global mask is replaced wholesale by the fine source's.
    /// </summary>
    internal async Task UpsampleTargetAsync(
        Direct3D12PatchWorkset coarse, Direct3D12PatchWorkset fine, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(coarse);
        ValidateOwnedWorkset(fine);
        if (fine.Width / 2 != coarse.Width || fine.Height / 2 != coarse.Height)
        {
            throw new ArgumentException("The fine workset must be the next finer pyramid level (2c or 2c + 1 pixels).",
                                        nameof(fine));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            coarse.ThrowIfDisposed();
            fine.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteUpsampleTargetAsync(coarse, fine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在 GPU 上并行随机初始化一个最近邻场。随机状态由全局 seed 和源像素索引派生。<br/>
    /// Initializes one nearest-neighbor field in parallel on the GPU. Random state is derived from the
    /// global seed and source-pixel index.
    /// </summary>
    internal async Task<Direct3D12NearestNeighborField> InitializeNearestNeighborFieldAsync(
        Direct3D12PatchWorkset workset, int patchRadius, uint randomSeed, bool sourceToTarget,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(workset);
        ValidatePatchRadius(patchRadius);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workset.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteInitializeNearestNeighborFieldAsync(workset, patchRadius, randomSeed,
                                                                     sourceToTarget, cancellationToken)
               .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 使用红黑棋盘并行传播和随机搜索原地最小化最近邻场。每个 pass 包含正向与反向扫描语义；
    /// 同色像素只依赖异色轴向邻居，两色 dispatch 之间使用 UAV 屏障。<br/>
    /// Minimizes a nearest-neighbor field in place with red-black parallel propagation and random search.
    /// Each pass contains forward and backward sweep semantics; same-color pixels only depend on opposite-color
    /// axial neighbors, with UAV barriers separating color dispatches.
    /// </summary>
    internal async Task MinimizeNearestNeighborFieldAsync(
        Direct3D12NearestNeighborField field, int passCount, uint randomSeed, bool conditionalSkip,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnedField(field);
        if (passCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passCount), passCount,
                                                  "The pass count cannot be negative.");
        }

        if (passCount == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteMinimizeNearestNeighborFieldAsync(field, passCount, randomSeed, conditionalSkip,
                                                           cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 对原始 source mask 周围不含孔洞的像素，把两个方向的链接都设为零距离恒等映射。<br/>
    /// Sets both field directions to zero-distance identity links for pixels whose original source-mask
    /// neighborhood contains no holes.
    /// </summary>
    internal async Task SetIdentityLinksAsync(
        Direct3D12NearestNeighborField sourceToTarget, Direct3D12NearestNeighborField targetToSource,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentityFieldPair(sourceToTarget, targetToSource);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            sourceToTarget.ThrowIfDisposed();
            targetToSource.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteSetIdentityLinksAsync(sourceToTarget, targetToSource, cancellationToken)
               .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 创建清零的定点投票缓冲。每个颜色通道和权重使用两个 uint32 原子槽组成 uint64 累加器。<br/>
    /// Creates a zeroed fixed-point vote buffer. Each color channel and weight uses two uint32 atomic lanes
    /// forming a uint64 accumulator.
    /// </summary>
    internal async Task<Direct3D12FixedPointVoteBuffer> CreateFixedPointVoteBufferAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(workset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workset.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteCreateFixedPointVoteBufferAsync(workset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 将一个最近邻场的非上采样投票累加到定点缓冲。正向场只接受孔洞中心，反向场只接受非孔洞中心。<br/>
    /// Accumulates one nearest-neighbor field's non-upscaled votes into a fixed-point buffer. The forward field
    /// accepts only hole centers and the reverse field only non-hole centers.
    /// </summary>
    internal async Task AccumulateFixedPointVotesAsync(
        Direct3D12NearestNeighborField field, Direct3D12FixedPointVoteBuffer votes,
        CancellationToken cancellationToken = default)
    {
        await AccumulateFixedPointVotesCoreAsync(field, votes, upscaled : false, cancellationToken)
           .ConfigureAwait(false);
    }

    /// <summary>
    /// 将粗层最近邻场按 2x2 对应关系投票到相邻细层，颜色及像素有效性取自细层原图。<br/>
    /// Transfers coarse-field votes to the next finer level using 2x2 correspondences and fine source pixels.
    /// </summary>
    internal async Task AccumulateUpscaledFixedPointVotesAsync(
        Direct3D12NearestNeighborField field, Direct3D12FixedPointVoteBuffer votes,
        CancellationToken cancellationToken = default)
    {
        await AccumulateFixedPointVotesCoreAsync(field, votes, upscaled : true, cancellationToken)
           .ConfigureAwait(false);
    }

    private async Task AccumulateFixedPointVotesCoreAsync(
        Direct3D12NearestNeighborField field, Direct3D12FixedPointVoteBuffer votes, bool upscaled,
        CancellationToken cancellationToken)
    {
        ValidateOwnedField(field);
        ValidateOwnedVoteBuffer(votes);
        if (upscaled)
        {
            if (ReferenceEquals(field.Workset, votes.Workset) ||
                votes.Workset.Width / 2 != field.Workset.Width ||
                votes.Workset.Height / 2 != field.Workset.Height)
            {
                throw new ArgumentException("The vote buffer must belong to the next finer pyramid level.",
                                            nameof(votes));
            }
        }
        else if (!ReferenceEquals(field.Workset, votes.Workset))
        {
            throw new ArgumentException("The nearest-neighbor field and vote buffer must share a workset.",
                                        nameof(votes));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field.ThrowIfDisposed();
            votes.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteAccumulateFixedPointVotesAsync(field, votes, upscaled, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 回读定点投票缓冲，仅供阶段 2 原型差分验证使用。<br/>
    /// Reads a fixed-point vote buffer for phase-2 prototype differential validation.
    /// </summary>
    internal async Task<FixedPointVote[]> ReadFixedPointVotesAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken = default)
    {
        ValidateOwnedVoteBuffer(votes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            votes.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteReadFixedPointVotesAsync(votes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在 GPU 上最大化定点投票并回读紧密排列的 RGB 像素。无投票或 global-mask 像素保留 target 值。<br/>
    /// Maximizes fixed-point votes on the GPU and reads back tightly packed RGB pixels. Pixels with no votes
    /// or a global mask preserve their target value.
    /// </summary>
    internal async Task<byte[]> MaximizeFixedPointVotesAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken = default)
    {
        ValidateOwnedVoteBuffer(votes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            votes.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteMaximizeFixedPointVotesAsync(votes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在 GPU 上最大化定点投票，并将紧密排列的 RGB 结果原地写回工作集 target。<br/>
    /// Maximizes fixed-point votes on the GPU and writes the tightly packed RGB result back to the workset target.
    /// </summary>
    internal async Task MaximizeFixedPointVotesInPlaceAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken = default)
    {
        ValidateOwnedVoteBuffer(votes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            votes.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteMaximizeFixedPointVotesInPlaceAsync(votes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 回读工作集当前 target RGB，仅供阶段 2 原型验证使用。<br/>
    /// Reads the workset's current target RGB for phase-2 prototype validation.
    /// </summary>
    internal async Task<byte[]> ReadTargetPixelsAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(workset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workset.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteReadTargetPixelsAsync(workset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 一次提交回读工作集的全部 7 个缓冲，仅供阶段 2 原型差分验证使用。<br/>
    /// Reads back all seven buffers of a workset in a single submission, for phase-2 prototype
    /// differential validation only.
    /// </summary>
    internal async Task<PatchWorksetSnapshot> ReadPatchWorksetAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(workset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workset.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteReadPatchWorksetAsync(workset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 执行阶段 2 的单层、单次非上采样 EM 原型。<br/>
    /// Runs the phase-2 single-level, single-iteration, non-upscaled EM prototype.
    /// </summary>
    internal Task<byte[]> RunNonUpscaledIterationAsync(
        Direct3D12PatchWorkset workset, int patchRadius, int passCount,
        uint initializationSeed, uint minimizationSeed,
        CancellationToken cancellationToken = default)
    {
        return RunNonUpscaledIterationsAsync(workset, patchRadius, iterationCount : 1, passCount,
                                              initializationSeed, minimizationSeed, cancellationToken);
    }

    /// <summary>
    /// 执行阶段 2 的单层、多次非上采样 EM 原型。迭代间 target 始终驻留 GPU，仅回读最终 RGB。<br/>
    /// Runs the phase-2 single-level, multi-iteration, non-upscaled EM prototype. The target remains GPU-resident
    /// between iterations and only the final RGB image is read back.
    /// </summary>
    internal async Task<byte[]> RunNonUpscaledIterationsAsync(
        Direct3D12PatchWorkset workset, int patchRadius, int iterationCount, int passCount,
        uint initializationSeed, uint minimizationSeed,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkset(workset);
        ValidatePatchRadius(patchRadius);
        if (iterationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterationCount), iterationCount,
                                                  "The iteration count must be positive.");
        }

        if (passCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passCount), passCount,
                                                  "The pass count cannot be negative.");
        }

        using var forward = await InitializeNearestNeighborFieldAsync(
            workset, patchRadius, initializationSeed, sourceToTarget : true, cancellationToken)
           .ConfigureAwait(false);
        using var reverse = await InitializeNearestNeighborFieldAsync(
            workset, patchRadius, initializationSeed, sourceToTarget : false, cancellationToken)
           .ConfigureAwait(false);
        for (var iteration = 0; iteration < iterationCount; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SetIdentityLinksAsync(forward, reverse, cancellationToken).ConfigureAwait(false);
            var iterationSeed = DeriveIterationSeed(minimizationSeed, iteration);
            await MinimizeNearestNeighborFieldAsync(
                forward, passCount, iterationSeed, conditionalSkip : true, cancellationToken).ConfigureAwait(false);
            await MinimizeNearestNeighborFieldAsync(
                reverse, passCount, iterationSeed, conditionalSkip : true, cancellationToken).ConfigureAwait(false);
            using var votes = await CreateFixedPointVoteBufferAsync(workset, cancellationToken).ConfigureAwait(false);
            await AccumulateFixedPointVotesAsync(forward, votes, cancellationToken).ConfigureAwait(false);
            await AccumulateFixedPointVotesAsync(reverse, votes, cancellationToken).ConfigureAwait(false);
            await MaximizeFixedPointVotesInPlaceAsync(votes, cancellationToken).ConfigureAwait(false);
        }

        return await ReadTargetPixelsAsync(workset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 回读最近邻场并按 CPU 距离语义归一化，仅供原型验证使用。<br/>
    /// Reads a nearest-neighbor field back and normalizes distances with CPU semantics for prototype validation.
    /// </summary>
    internal async Task<NearestNeighborLink[]> ReadNearestNeighborFieldAsync(
        Direct3D12NearestNeighborField field, CancellationToken cancellationToken = default)
    {
        ValidateOwnedField(field);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteReadNearestNeighborFieldAsync(field, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<uint[]> ExecuteSquareDifferencesAsync(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right,
                                                             CancellationToken cancellationToken)
    {
        var inputByteCount  = checked((ulong)((left.Length + 3) & ~3));
        var outputByteCount = checked((ulong)left.Length * sizeof(uint));

        using var leftBuffer  = CreateUploadBuffer(left.Span, inputByteCount);
        using var rightBuffer = CreateUploadBuffer(right.Span, inputByteCount);
        using var outputBuffer = CreateBuffer(outputByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
        using var readbackBuffer =
            CreateBuffer(outputByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                                                                  _squareDifferencePipelineState);

        commandList.SetComputeRootSignature(_squareDifferenceRootSignature);
        commandList.SetComputeRootShaderResourceView(0, leftBuffer.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, rightBuffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(2, outputBuffer.GPUVirtualAddress);
        var groupCountX = (int)Math.Min(((long)left.Length + ShaderThreadCount - 1) / ShaderThreadCount,
                                        MaxDispatchGroupsPerDimension);
        var dispatchWidth = checked(groupCountX * ShaderThreadCount);
        var groupCountY = (int)(((long)left.Length + dispatchWidth - 1) / dispatchWidth);
        commandList.SetComputeRoot32BitConstant(3, (uint)left.Length, 0);
        commandList.SetComputeRoot32BitConstant(3, (uint)dispatchWidth, 1);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(outputBuffer, ResourceStates.UnorderedAccess,
                                                                     ResourceStates.CopySource));
        commandList.CopyBufferRegion(readbackBuffer, 0, outputBuffer, 0, outputByteCount);
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();

        // A submitted command list still owns all resources above. Cancellation therefore stops future work,
        // but resource reclamation must wait for the GPU fence before OperationCanceledException is observed.
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var result = new uint[left.Length];
        readbackBuffer.Map<uint>(0, result.Length).CopyTo(result);
        readbackBuffer.Unmap(0);
        return result;
    }

    private async Task<int[]> ExecutePatchDistancesAsync(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceInvalid,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetInvalid,
        int width, int height, ReadOnlyMemory<PatchPair> pairs, int patchRadius,
        CancellationToken cancellationToken)
    {
        var pairBytes       = MemoryMarshal.AsBytes(pairs.Span).ToArray();
        var outputByteCount = checked((ulong)pairs.Length * sizeof(uint) * 2);

        using var sourcePixelsBuffer = CreateUploadBuffer(sourcePixels.Span, AlignToFour(sourcePixels.Length));
        using var sourceInvalidBuffer = CreateUploadBuffer(sourceInvalid.Span, AlignToFour(sourceInvalid.Length));
        using var targetPixelsBuffer = CreateUploadBuffer(targetPixels.Span, AlignToFour(targetPixels.Length));
        using var targetInvalidBuffer = CreateUploadBuffer(targetInvalid.Span, AlignToFour(targetInvalid.Length));
        using var pairsBuffer = CreateUploadBuffer(pairBytes, AlignToFour(pairBytes.Length));
        using var outputBuffer = CreateBuffer(outputByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
        using var readbackBuffer =
            CreateBuffer(outputByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                                                                  _patchDistancePipelineState);

        commandList.SetComputeRootSignature(_patchDistanceRootSignature);
        commandList.SetComputeRootShaderResourceView(0, sourcePixelsBuffer.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, sourceInvalidBuffer.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(2, targetPixelsBuffer.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(3, targetInvalidBuffer.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(4, pairsBuffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(5, outputBuffer.GPUVirtualAddress);
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pairs.Length);
        commandList.SetComputeRoot32BitConstant(6, (uint)pairs.Length, 0);
        commandList.SetComputeRoot32BitConstant(6, (uint)width, 1);
        commandList.SetComputeRoot32BitConstant(6, (uint)height, 2);
        commandList.SetComputeRoot32BitConstant(6, (uint)patchRadius, 3);
        commandList.SetComputeRoot32BitConstant(6, (uint)dispatchWidth, 4);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(outputBuffer, ResourceStates.UnorderedAccess,
                                                                     ResourceStates.CopySource));
        commandList.CopyBufferRegion(readbackBuffer, 0, outputBuffer, 0, outputByteCount);
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var accumulators = new uint[checked(pairs.Length * 2)];
        readbackBuffer.Map<uint>(0, accumulators.Length).CopyTo(accumulators);
        readbackBuffer.Unmap(0);

        var result      = new int[pairs.Length];
        var patchWidth  = 2L * patchRadius + 1;
        var patchPixels = patchWidth * patchWidth;
        for (var index = 0; index < result.Length; index++)
        {
            var squaredSum = accumulators[index * 2];
            var validPixels = accumulators[index * 2 + 1];
            double distance = SsdScale * (patchPixels - validPixels) + squaredSum;
            distance /= SsdScale;
            var value = (int)(DistanceScale * distance / patchPixels);
            result[index] = value is < 0 or > DistanceScale ? DistanceScale : value;
        }

        return result;
    }

    private async Task<Direct3D12PatchWorkset> ExecuteCreatePatchWorksetAsync(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceMask, ReadOnlyMemory<byte> sourceInvalid,
        ReadOnlyMemory<byte> sourceGlobal,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetInvalid, ReadOnlyMemory<byte> targetGlobal,
        int width, int height, CancellationToken cancellationToken)
    {
        ID3D12Resource? sourcePixelsBuffer  = null;
        ID3D12Resource? sourceMaskBuffer    = null;
        ID3D12Resource? sourceInvalidBuffer = null;
        ID3D12Resource? sourceGlobalBuffer  = null;
        ID3D12Resource? targetPixelsBuffer  = null;
        ID3D12Resource? targetInvalidBuffer = null;
        ID3D12Resource? targetGlobalBuffer  = null;
        try
        {
            var imageByteCount = AlignToFour(sourcePixels.Length);
            var maskByteCount  = AlignToFour(sourceInvalid.Length);
            sourcePixelsBuffer = CreateBuffer(imageByteCount, HeapType.Default, ResourceStates.CopyDest,
                                              ResourceFlags.AllowUnorderedAccess);
            sourceMaskBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.CopyDest,
                                            ResourceFlags.AllowUnorderedAccess);
            sourceInvalidBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.CopyDest,
                                               ResourceFlags.AllowUnorderedAccess);
            sourceGlobalBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.CopyDest,
                                              ResourceFlags.AllowUnorderedAccess);
            targetPixelsBuffer = CreateBuffer(imageByteCount, HeapType.Default, ResourceStates.CopyDest,
                                              ResourceFlags.AllowUnorderedAccess);
            targetInvalidBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.CopyDest,
                                               ResourceFlags.AllowUnorderedAccess);
            targetGlobalBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.CopyDest,
                                              ResourceFlags.AllowUnorderedAccess);

            using var sourcePixelsUpload  = CreateUploadBuffer(sourcePixels.Span, imageByteCount);
            using var sourceMaskUpload    = CreateUploadBuffer(sourceMask.Span, maskByteCount);
            using var sourceInvalidUpload = CreateUploadBuffer(sourceInvalid.Span, maskByteCount);
            using var sourceGlobalUpload  = CreateUploadBuffer(sourceGlobal.Span, maskByteCount);
            using var targetPixelsUpload  = CreateUploadBuffer(targetPixels.Span, imageByteCount);
            using var targetInvalidUpload = CreateUploadBuffer(targetInvalid.Span, maskByteCount);
            using var targetGlobalUpload  = CreateUploadBuffer(targetGlobal.Span, maskByteCount);
            using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
            using var commandList =
                _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                    _nearestNeighborInitializationPipelineState);

            commandList.CopyBufferRegion(sourcePixelsBuffer, 0, sourcePixelsUpload, 0, imageByteCount);
            commandList.CopyBufferRegion(sourceMaskBuffer, 0, sourceMaskUpload, 0, maskByteCount);
            commandList.CopyBufferRegion(sourceInvalidBuffer, 0, sourceInvalidUpload, 0, maskByteCount);
            commandList.CopyBufferRegion(sourceGlobalBuffer, 0, sourceGlobalUpload, 0, maskByteCount);
            commandList.CopyBufferRegion(targetPixelsBuffer, 0, targetPixelsUpload, 0, imageByteCount);
            commandList.CopyBufferRegion(targetInvalidBuffer, 0, targetInvalidUpload, 0, maskByteCount);
            commandList.CopyBufferRegion(targetGlobalBuffer, 0, targetGlobalUpload, 0, maskByteCount);
            TransitionToShaderResource(commandList, sourcePixelsBuffer);
            TransitionToShaderResource(commandList, sourceMaskBuffer);
            TransitionToShaderResource(commandList, sourceInvalidBuffer);
            TransitionToShaderResource(commandList, sourceGlobalBuffer);
            TransitionToShaderResource(commandList, targetPixelsBuffer);
            TransitionToShaderResource(commandList, targetInvalidBuffer);
            TransitionToShaderResource(commandList, targetGlobalBuffer);
            commandList.Close();

            _commandQueue.ExecuteCommandList(commandList);
            var fenceValue = _nextFenceValue++;
            _commandQueue.Signal(_fence, fenceValue).CheckError();
            if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _asynchronousFenceWaitCount);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new Direct3D12PatchWorkset(this, width, height,
                                              sourcePixelsBuffer, sourceMaskBuffer, sourceInvalidBuffer,
                                              sourceGlobalBuffer,
                                              targetPixelsBuffer, targetInvalidBuffer, targetGlobalBuffer);
        }
        catch
        {
            targetGlobalBuffer?.Dispose();
            targetInvalidBuffer?.Dispose();
            targetPixelsBuffer?.Dispose();
            sourceGlobalBuffer?.Dispose();
            sourceInvalidBuffer?.Dispose();
            sourceMaskBuffer?.Dispose();
            sourcePixelsBuffer?.Dispose();
            throw;
        }
    }

    private async Task<Direct3D12NearestNeighborField> ExecuteInitializeNearestNeighborFieldAsync(
        Direct3D12PatchWorkset workset, int patchRadius, uint randomSeed, bool sourceToTarget,
        CancellationToken cancellationToken)
    {
        var pixelCount     = checked(workset.Width * workset.Height);
        var fieldByteCount = checked((ulong)pixelCount * sizeof(uint) * 3);
        ID3D12Resource? fieldBuffer = null;
        try
        {
            fieldBuffer = CreateBuffer(fieldByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                       ResourceFlags.AllowUnorderedAccess);
            using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
            using var commandList =
                _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                    _nearestNeighborInitializationPipelineState);

            var sourcePixels  = sourceToTarget ? workset.SourcePixels : workset.TargetPixels;
            var sourceInvalid = sourceToTarget ? workset.SourceInvalid : workset.TargetInvalid;
            var sourceGlobal  = sourceToTarget ? workset.SourceGlobal : workset.TargetGlobal;
            var targetPixels  = sourceToTarget ? workset.TargetPixels : workset.SourcePixels;
            var targetInvalid = sourceToTarget ? workset.TargetInvalid : workset.SourceInvalid;
            var targetGlobal  = sourceToTarget ? workset.TargetGlobal : workset.SourceGlobal;

            commandList.SetComputeRootSignature(_nearestNeighborInitializationRootSignature);
            commandList.SetComputeRootShaderResourceView(0, sourcePixels.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(1, sourceInvalid.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(2, sourceGlobal.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(3, targetPixels.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(4, targetInvalid.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(5, targetGlobal.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(6, fieldBuffer.GPUVirtualAddress);
            var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pixelCount);
            commandList.SetComputeRoot32BitConstant(7, (uint)workset.Width, 0);
            commandList.SetComputeRoot32BitConstant(7, (uint)workset.Height, 1);
            commandList.SetComputeRoot32BitConstant(7, (uint)patchRadius, 2);
            commandList.SetComputeRoot32BitConstant(7, randomSeed, 3);
            commandList.SetComputeRoot32BitConstant(7, (uint)dispatchWidth, 4);
            commandList.SetComputeRoot32BitConstant(7, MaximumInitializationAttempts, 5);
            commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
            commandList.Close();

            _commandQueue.ExecuteCommandList(commandList);
            var fenceValue = _nextFenceValue++;
            _commandQueue.Signal(_fence, fenceValue).CheckError();
            if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _asynchronousFenceWaitCount);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new Direct3D12NearestNeighborField(this, workset, fieldBuffer, patchRadius, sourceToTarget);
        }
        catch
        {
            fieldBuffer?.Dispose();
            throw;
        }
    }

    private async Task<NearestNeighborLink[]> ExecuteReadNearestNeighborFieldAsync(
        Direct3D12NearestNeighborField field, CancellationToken cancellationToken)
    {
        var pixelCount     = checked(field.Workset.Width * field.Workset.Height);
        var fieldByteCount = checked((ulong)pixelCount * sizeof(uint) * 3);
        using var readbackBuffer =
            CreateBuffer(fieldByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _nearestNeighborInitializationPipelineState);

        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(field.Buffer,
                                                                       ResourceStates.UnorderedAccess,
                                                                       ResourceStates.CopySource));
        commandList.CopyBufferRegion(readbackBuffer, 0, field.Buffer, 0, fieldByteCount);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(field.Buffer, ResourceStates.CopySource,
                                                                       ResourceStates.UnorderedAccess));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var rawField = new uint[checked(pixelCount * 3)];
        readbackBuffer.Map<uint>(0, rawField.Length).CopyTo(rawField);
        readbackBuffer.Unmap(0);

        var links       = new NearestNeighborLink[pixelCount];
        var patchWidth  = 2L * field.PatchRadius + 1;
        var patchPixels = patchWidth * patchWidth;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 3;
            double distance = rawField[offset + 2];
            distance /= SsdScale;
            var normalized = (int)(DistanceScale * distance / patchPixels);
            normalized = normalized is < 0 or > DistanceScale ? DistanceScale : normalized;
            links[pixel] = new NearestNeighborLink(checked((int)rawField[offset]),
                                                   checked((int)rawField[offset + 1]), normalized);
        }

        return links;
    }

    private async Task ExecuteMinimizeNearestNeighborFieldAsync(
        Direct3D12NearestNeighborField field, int passCount, uint randomSeed, bool conditionalSkip,
        CancellationToken cancellationToken)
    {
        var workset   = field.Workset;
        var pixelCount = checked(workset.Width * workset.Height);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _nearestNeighborMinimizationPipelineState);

        var sourcePixels  = field.SourceToTarget ? workset.SourcePixels : workset.TargetPixels;
        var sourceInvalid = field.SourceToTarget ? workset.SourceInvalid : workset.TargetInvalid;
        var sourceGlobal  = field.SourceToTarget ? workset.SourceGlobal : workset.TargetGlobal;
        var targetPixels  = field.SourceToTarget ? workset.TargetPixels : workset.SourcePixels;
        var targetInvalid = field.SourceToTarget ? workset.TargetInvalid : workset.SourceInvalid;
        var targetGlobal  = field.SourceToTarget ? workset.TargetGlobal : workset.SourceGlobal;
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pixelCount);

        commandList.SetComputeRootSignature(_nearestNeighborMinimizationRootSignature);
        commandList.SetComputeRootShaderResourceView(0, sourcePixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, sourceInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(2, sourceGlobal.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(3, targetPixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(4, targetInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(5, targetGlobal.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(6, field.Buffer.GPUVirtualAddress);
        commandList.SetComputeRoot32BitConstant(7, (uint)workset.Width, 0);
        commandList.SetComputeRoot32BitConstant(7, (uint)workset.Height, 1);
        commandList.SetComputeRoot32BitConstant(7, (uint)field.PatchRadius, 2);
        commandList.SetComputeRoot32BitConstant(7, randomSeed, 3);
        commandList.SetComputeRoot32BitConstant(7, (uint)dispatchWidth, 4);
        commandList.SetComputeRoot32BitConstant(7, conditionalSkip ? 1u : 0u, 7);
        commandList.SetComputeRoot32BitConstant(7, field.SourceToTarget ? 1u : 0u, 8);

        uint sweepIndex = 0;
        for (var pass = 0; pass < passCount; pass++)
        {
            DispatchMinimizationColor(commandList, groupCountX, groupCountY,
                                      propagationDirection : 1, checkerboardParity : 0, sweepIndex++);
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(field.Buffer));
            DispatchMinimizationColor(commandList, groupCountX, groupCountY,
                                      propagationDirection : 1, checkerboardParity : 1, sweepIndex++);
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(field.Buffer));
            DispatchMinimizationColor(commandList, groupCountX, groupCountY,
                                      propagationDirection : -1, checkerboardParity : 1, sweepIndex++);
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(field.Buffer));
            DispatchMinimizationColor(commandList, groupCountX, groupCountY,
                                      propagationDirection : -1, checkerboardParity : 0, sweepIndex++);
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(field.Buffer));
        }

        commandList.Close();
        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void DispatchMinimizationColor(
        ID3D12GraphicsCommandList commandList, int groupCountX, int groupCountY,
        int propagationDirection, uint checkerboardParity, uint sweepIndex)
    {
        commandList.SetComputeRoot32BitConstant(7, unchecked((uint)propagationDirection), 5);
        commandList.SetComputeRoot32BitConstant(7, checkerboardParity, 6);
        commandList.SetComputeRoot32BitConstant(7, sweepIndex, 9);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
    }

    private async Task ExecuteSetIdentityLinksAsync(
        Direct3D12NearestNeighborField sourceToTarget, Direct3D12NearestNeighborField targetToSource,
        CancellationToken cancellationToken)
    {
        var workset = sourceToTarget.Workset;
        var pixelCount = checked(workset.Width * workset.Height);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _setIdentityLinksPipelineState);
        commandList.SetComputeRootSignature(_setIdentityLinksRootSignature);
        commandList.SetComputeRootShaderResourceView(0, workset.SourceInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, workset.SourceGlobal.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(2, sourceToTarget.Buffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(3, targetToSource.Buffer.GPUVirtualAddress);
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pixelCount);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Width, 0);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Height, 1);
        commandList.SetComputeRoot32BitConstant(4, (uint)sourceToTarget.PatchRadius, 2);
        commandList.SetComputeRoot32BitConstant(4, (uint)dispatchWidth, 3);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(sourceToTarget.Buffer));
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(targetToSource.Buffer));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<Direct3D12FixedPointVoteBuffer> ExecuteCreateFixedPointVoteBufferAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken)
    {
        var pixelCount = checked(workset.Width * workset.Height);
        var byteCount = checked((ulong)pixelCount * Direct3D12FixedPointVoteBuffer.BytesPerPixel);
        ID3D12Resource? buffer = null;
        try
        {
            buffer = CreateBuffer(byteCount, HeapType.Default, ResourceStates.CopyDest,
                                  ResourceFlags.AllowUnorderedAccess);
            using var zeroUpload = CreateUploadBuffer(new byte[checked((int)byteCount)], byteCount);
            using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
            using var commandList =
                _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                    _fixedPointVotePipelineState);
            commandList.CopyBufferRegion(buffer, 0, zeroUpload, 0, byteCount);
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                buffer, ResourceStates.CopyDest, ResourceStates.UnorderedAccess));
            commandList.Close();

            _commandQueue.ExecuteCommandList(commandList);
            var fenceValue = _nextFenceValue++;
            _commandQueue.Signal(_fence, fenceValue).CheckError();
            if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _asynchronousFenceWaitCount);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new Direct3D12FixedPointVoteBuffer(this, workset, buffer);
        }
        catch
        {
            buffer?.Dispose();
            throw;
        }
    }

    private async Task ExecuteAccumulateFixedPointVotesAsync(
        Direct3D12NearestNeighborField field, Direct3D12FixedPointVoteBuffer votes,
        bool upscaled, CancellationToken cancellationToken)
    {
        var workset = field.Workset;
        var pixelCount = checked(workset.Width * workset.Height);
        var fieldSourceInvalid = field.SourceToTarget ? workset.SourceInvalid : workset.TargetInvalid;
        var fieldSourceGlobal  = field.SourceToTarget ? workset.SourceGlobal : workset.TargetGlobal;
        var fieldTargetGlobal  = field.SourceToTarget ? workset.TargetGlobal : workset.SourceGlobal;
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _fixedPointVotePipelineState);

        commandList.SetComputeRootSignature(_fixedPointVoteRootSignature);
        commandList.SetComputeRootShaderResourceView(0, votes.Workset.SourcePixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, votes.Workset.SourceInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(2, fieldSourceInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(3, fieldSourceGlobal.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(4, fieldTargetGlobal.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(5, _similarityWeightsBuffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(6, field.Buffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(7, votes.Buffer.GPUVirtualAddress);
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pixelCount);
        commandList.SetComputeRoot32BitConstant(8, (uint)workset.Width, 0);
        commandList.SetComputeRoot32BitConstant(8, (uint)workset.Height, 1);
        commandList.SetComputeRoot32BitConstant(8, (uint)field.PatchRadius, 2);
        commandList.SetComputeRoot32BitConstant(8, (uint)dispatchWidth, 3);
        commandList.SetComputeRoot32BitConstant(8, field.SourceToTarget ? 1u : 0u, 4);
        commandList.SetComputeRoot32BitConstant(8, (uint)votes.Workset.Width, 5);
        commandList.SetComputeRoot32BitConstant(8, (uint)votes.Workset.Height, 6);
        commandList.SetComputeRoot32BitConstant(8, upscaled ? 2u : 1u, 7);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(votes.Buffer));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<FixedPointVote[]> ExecuteReadFixedPointVotesAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken)
    {
        var pixelCount = checked(votes.Workset.Width * votes.Workset.Height);
        var rawLength = checked(pixelCount * 8);
        var byteCount = checked((ulong)rawLength * sizeof(uint));
        using var readback = CreateBuffer(byteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _fixedPointVotePipelineState);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            votes.Buffer, ResourceStates.UnorderedAccess, ResourceStates.CopySource));
        commandList.CopyBufferRegion(readback, 0, votes.Buffer, 0, byteCount);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            votes.Buffer, ResourceStates.CopySource, ResourceStates.UnorderedAccess));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var raw = new uint[rawLength];
        readback.Map<uint>(0, rawLength).CopyTo(raw);
        readback.Unmap(0);

        var result = new FixedPointVote[pixelCount];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 8;
            result[pixel] = new FixedPointVote(
                CombineAtomicLanes(raw[offset], raw[offset + 1]),
                CombineAtomicLanes(raw[offset + 2], raw[offset + 3]),
                CombineAtomicLanes(raw[offset + 4], raw[offset + 5]),
                CombineAtomicLanes(raw[offset + 6], raw[offset + 7]));
        }

        return result;
    }

    private async Task<byte[]> ExecuteMaximizeFixedPointVotesAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken)
    {
        var workset = votes.Workset;
        var pixelCount = checked(workset.Width * workset.Height);
        var outputByteCount = checked((ulong)pixelCount * sizeof(uint));
        using var output = CreateBuffer(outputByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                        ResourceFlags.AllowUnorderedAccess);
        using var readback = CreateBuffer(outputByteCount, HeapType.Readback, ResourceStates.CopyDest,
                                          ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _fixedPointMaximizePipelineState);
        commandList.SetComputeRootSignature(_fixedPointMaximizeRootSignature);
        commandList.SetComputeRootShaderResourceView(0, workset.TargetPixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, workset.TargetGlobal.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(2, votes.Buffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(3, output.GPUVirtualAddress);
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(pixelCount);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Width, 0);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Height, 1);
        commandList.SetComputeRoot32BitConstant(4, (uint)dispatchWidth, 2);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            output, ResourceStates.UnorderedAccess, ResourceStates.CopySource));
        commandList.CopyBufferRegion(readback, 0, output, 0, outputByteCount);
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packed = new uint[pixelCount];
        readback.Map<uint>(0, pixelCount).CopyTo(packed);
        readback.Unmap(0);
        var result = new byte[checked(pixelCount * 3)];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var outputOffset = pixel * 3;
            result[outputOffset]     = (byte)packed[pixel];
            result[outputOffset + 1] = (byte)(packed[pixel] >> 8);
            result[outputOffset + 2] = (byte)(packed[pixel] >> 16);
        }

        return result;
    }

    private async Task ExecuteMaximizeFixedPointVotesInPlaceAsync(
        Direct3D12FixedPointVoteBuffer votes, CancellationToken cancellationToken)
    {
        var workset = votes.Workset;
        var pixelCount = checked(workset.Width * workset.Height);
        var imageByteCount = checked(pixelCount * 3);
        var packedByteCount = checked((ulong)pixelCount * sizeof(uint));
        using var packedOutput = CreateBuffer(packedByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _fixedPointMaximizePipelineState);

        commandList.SetComputeRootSignature(_fixedPointMaximizeRootSignature);
        commandList.SetComputeRootShaderResourceView(0, workset.TargetPixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, workset.TargetGlobal.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(2, votes.Buffer.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(3, packedOutput.GPUVirtualAddress);
        var (maximizeGroupCountX, maximizeGroupCountY, maximizeDispatchWidth) =
            GetDispatchDimensions(pixelCount);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Width, 0);
        commandList.SetComputeRoot32BitConstant(4, (uint)workset.Height, 1);
        commandList.SetComputeRoot32BitConstant(4, (uint)maximizeDispatchWidth, 2);
        commandList.Dispatch((uint)maximizeGroupCountX, (uint)maximizeGroupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(packedOutput));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            packedOutput, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            workset.TargetPixels, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));

        commandList.SetPipelineState(_unpackRgbPipelineState);
        commandList.SetComputeRootSignature(_unpackRgbRootSignature);
        commandList.SetComputeRootShaderResourceView(0, packedOutput.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(1, workset.TargetPixels.GPUVirtualAddress);
        var outputWordCount = checked((imageByteCount + sizeof(uint) - 1) / sizeof(uint));
        var (unpackGroupCountX, unpackGroupCountY, unpackDispatchWidth) =
            GetDispatchDimensions(outputWordCount);
        commandList.SetComputeRoot32BitConstant(2, (uint)imageByteCount, 0);
        commandList.SetComputeRoot32BitConstant(2, (uint)unpackDispatchWidth, 1);
        commandList.Dispatch((uint)unpackGroupCountX, (uint)unpackGroupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(workset.TargetPixels));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            workset.TargetPixels, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<byte[]> ExecuteReadTargetPixelsAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken)
    {
        var imageByteCount = checked(workset.Width * workset.Height * 3);
        var alignedByteCount = AlignToFour(imageByteCount);
        using var readback = CreateBuffer(alignedByteCount, HeapType.Readback, ResourceStates.CopyDest,
                                          ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _unpackRgbPipelineState);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            workset.TargetPixels, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource));
        commandList.CopyBufferRegion(readback, 0, workset.TargetPixels, 0, alignedByteCount);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            workset.TargetPixels, ResourceStates.CopySource, ResourceStates.NonPixelShaderResource));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = new byte[imageByteCount];
        readback.Map<byte>(0, imageByteCount).CopyTo(result);
        readback.Unmap(0);
        return result;
    }

    private async Task<Direct3D12PatchWorkset> ExecuteCreateDownsampledPatchWorksetAsync(
        Direct3D12PatchWorkset coarse, CancellationToken cancellationToken)
    {
        var width          = coarse.Width / 2;
        var height         = coarse.Height / 2;
        var pixelCount     = checked(width * height);
        var imageByteCount = AlignToFour(checked(pixelCount * 3));
        var maskByteCount  = AlignToFour(pixelCount);
        ID3D12Resource? sourcePixelsBuffer  = null;
        ID3D12Resource? sourceMaskBuffer    = null;
        ID3D12Resource? sourceInvalidBuffer = null;
        ID3D12Resource? sourceGlobalBuffer  = null;
        ID3D12Resource? targetPixelsBuffer  = null;
        ID3D12Resource? targetInvalidBuffer = null;
        ID3D12Resource? targetGlobalBuffer  = null;
        try
        {
            sourcePixelsBuffer = CreateBuffer(imageByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
            sourceMaskBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                            ResourceFlags.AllowUnorderedAccess);
            sourceInvalidBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                               ResourceFlags.AllowUnorderedAccess);
            sourceGlobalBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
            targetPixelsBuffer = CreateBuffer(imageByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
            targetInvalidBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                               ResourceFlags.AllowUnorderedAccess);
            targetGlobalBuffer = CreateBuffer(maskByteCount, HeapType.Default, ResourceStates.UnorderedAccess,
                                              ResourceFlags.AllowUnorderedAccess);
            using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
            using var commandList =
                _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                    _downsamplePipelineState);

            commandList.SetComputeRootSignature(_downsampleRootSignature);
            commandList.SetComputeRootShaderResourceView(0, coarse.SourcePixels.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(1, coarse.SourceMask.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(2, coarse.SourceGlobal.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(3, sourcePixelsBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(4, sourceMaskBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(5, sourceGlobalBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(6, sourceInvalidBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(7, targetPixelsBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(8, targetInvalidBuffer.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(9, targetGlobalBuffer.GPUVirtualAddress);
            var imageWordCount = checked((int)(imageByteCount / sizeof(uint)));
            var maskWordCount  = checked((int)(maskByteCount / sizeof(uint)));
            var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(imageWordCount + maskWordCount);
            commandList.SetComputeRoot32BitConstant(10, (uint)coarse.Width, 0);
            commandList.SetComputeRoot32BitConstant(10, (uint)coarse.Height, 1);
            commandList.SetComputeRoot32BitConstant(10, (uint)dispatchWidth, 2);
            commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(sourcePixelsBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(sourceMaskBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(sourceGlobalBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(sourceInvalidBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(targetPixelsBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(targetInvalidBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(targetGlobalBuffer));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                sourcePixelsBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                sourceMaskBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                sourceGlobalBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                sourceInvalidBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                targetPixelsBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                targetInvalidBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                targetGlobalBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
            commandList.Close();

            _commandQueue.ExecuteCommandList(commandList);
            var fenceValue = _nextFenceValue++;
            _commandQueue.Signal(_fence, fenceValue).CheckError();
            if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _asynchronousFenceWaitCount);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new Direct3D12PatchWorkset(this, width, height,
                                              sourcePixelsBuffer, sourceMaskBuffer, sourceInvalidBuffer,
                                              sourceGlobalBuffer,
                                              targetPixelsBuffer, targetInvalidBuffer, targetGlobalBuffer);
        }
        catch
        {
            targetGlobalBuffer?.Dispose();
            targetInvalidBuffer?.Dispose();
            targetPixelsBuffer?.Dispose();
            sourceGlobalBuffer?.Dispose();
            sourceInvalidBuffer?.Dispose();
            sourceMaskBuffer?.Dispose();
            sourcePixelsBuffer?.Dispose();
            throw;
        }
    }

    private async Task ExecuteUpsampleTargetAsync(
        Direct3D12PatchWorkset coarse, Direct3D12PatchWorkset fine, CancellationToken cancellationToken)
    {
        var pixelCount     = checked(fine.Width * fine.Height);
        var imageWordCount = checked((int)(AlignToFour(checked(pixelCount * 3)) / sizeof(uint)));
        var maskWordCount  = checked((int)(AlignToFour(pixelCount) / sizeof(uint)));
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _upsampleTargetPipelineState);

        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetPixels, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetInvalid, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetGlobal, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));

        commandList.SetComputeRootSignature(_upsampleTargetRootSignature);
        commandList.SetComputeRootShaderResourceView(0, coarse.TargetPixels.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, coarse.TargetInvalid.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(2, coarse.TargetGlobal.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(3, fine.SourceGlobal.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(4, fine.TargetPixels.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(5, fine.TargetInvalid.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(6, fine.TargetGlobal.GPUVirtualAddress);
        var (groupCountX, groupCountY, dispatchWidth) = GetDispatchDimensions(imageWordCount + maskWordCount);
        commandList.SetComputeRoot32BitConstant(7, (uint)coarse.Width, 0);
        commandList.SetComputeRoot32BitConstant(7, (uint)coarse.Height, 1);
        commandList.SetComputeRoot32BitConstant(7, (uint)fine.Width, 2);
        commandList.SetComputeRoot32BitConstant(7, (uint)fine.Height, 3);
        commandList.SetComputeRoot32BitConstant(7, (uint)dispatchWidth, 4);
        commandList.Dispatch((uint)groupCountX, (uint)groupCountY, 1);
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(fine.TargetPixels));
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(fine.TargetInvalid));
        commandList.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(fine.TargetGlobal));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetPixels, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetInvalid, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            fine.TargetGlobal, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<PatchWorksetSnapshot> ExecuteReadPatchWorksetAsync(
        Direct3D12PatchWorkset workset, CancellationToken cancellationToken)
    {
        var pixelCount            = checked(workset.Width * workset.Height);
        var imageByteCount        = checked(pixelCount * 3);
        var alignedImageByteCount = AlignToFour(imageByteCount);
        var alignedMaskByteCount  = AlignToFour(pixelCount);
        using var sourcePixelsReadback =
            CreateBuffer(alignedImageByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var sourceMaskReadback =
            CreateBuffer(alignedMaskByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var sourceInvalidReadback =
            CreateBuffer(alignedMaskByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var sourceGlobalReadback =
            CreateBuffer(alignedMaskByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var targetPixelsReadback =
            CreateBuffer(alignedImageByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var targetInvalidReadback =
            CreateBuffer(alignedMaskByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var targetGlobalReadback =
            CreateBuffer(alignedMaskByteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);
        using var allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        using var commandList =
            _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, allocator,
                _unpackRgbPipelineState);

        CopyToReadback(commandList, workset.SourcePixels, sourcePixelsReadback, alignedImageByteCount);
        CopyToReadback(commandList, workset.SourceMask, sourceMaskReadback, alignedMaskByteCount);
        CopyToReadback(commandList, workset.SourceInvalid, sourceInvalidReadback, alignedMaskByteCount);
        CopyToReadback(commandList, workset.SourceGlobal, sourceGlobalReadback, alignedMaskByteCount);
        CopyToReadback(commandList, workset.TargetPixels, targetPixelsReadback, alignedImageByteCount);
        CopyToReadback(commandList, workset.TargetInvalid, targetInvalidReadback, alignedMaskByteCount);
        CopyToReadback(commandList, workset.TargetGlobal, targetGlobalReadback, alignedMaskByteCount);
        commandList.Close();

        _commandQueue.ExecuteCommandList(commandList);
        var fenceValue = _nextFenceValue++;
        _commandQueue.Signal(_fence, fenceValue).CheckError();
        if (await FenceWaiter.WaitAsync(_fence, fenceValue).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _asynchronousFenceWaitCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PatchWorksetSnapshot(
            ReadBackBytes(sourcePixelsReadback, imageByteCount),
            ReadBackBytes(sourceMaskReadback, pixelCount),
            ReadBackBytes(sourceInvalidReadback, pixelCount),
            ReadBackBytes(sourceGlobalReadback, pixelCount),
            ReadBackBytes(targetPixelsReadback, imageByteCount),
            ReadBackBytes(targetInvalidReadback, pixelCount),
            ReadBackBytes(targetGlobalReadback, pixelCount));
    }

    private static ulong CombineAtomicLanes(uint low, uint high) => ((ulong)high << 32) | low;

    private static uint DeriveIterationSeed(uint seed, int iteration)
    {
        return iteration == 0 ? seed : unchecked(seed ^ ((uint)iteration * 0x9e37_79b9u));
    }

    internal static ulong GetWorstCaseFixedPointChannelSum(int pixelCount, int fieldCount)
    {
        if (pixelCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelCount));
        }

        if (fieldCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldCount));
        }

        return checked((ulong)pixelCount * (uint)fieldCount * byte.MaxValue *
                       Direct3D12FixedPointVoteBuffer.WeightScale);
    }

    private static uint[] BuildSimilarityWeights()
    {
        double[] samples = [1.0, 0.99, 0.96, 0.83, 0.38, 0.11, 0.02, 0.005, 0.0006, 0.0001, 0];
        var result = new uint[DistanceScale + 1];
        for (var distance = 0; distance < result.Length; distance++)
        {
            var normalized = (double)distance / result.Length;
            var lowerIndex = (int)(100 * normalized);
            var upperIndex = lowerIndex + 1;
            var lower = lowerIndex < samples.Length ? samples[lowerIndex] : 0;
            var upper = upperIndex < samples.Length ? samples[upperIndex] : 0;
            var similarity = lower + (((100 * normalized) - lowerIndex) * (upper - lower));
            result[distance] = checked((uint)Math.Round(
                similarity * Direct3D12FixedPointVoteBuffer.WeightScale, MidpointRounding.ToEven));
        }

        return result;
    }

    private static void ValidatePatchDistanceInputs(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceInvalid,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetInvalid,
        int width, int height, int patchRadius)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        int pixelCount;
        int imageByteCount;
        try
        {
            pixelCount     = checked(width * height);
            imageByteCount = checked(pixelCount * 3);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The supplied dimensions are too large.");
        }

        if (sourcePixels.Length != imageByteCount)
        {
            throw new ArgumentException($"The source image must contain exactly {imageByteCount} bytes.",
                                        nameof(sourcePixels));
        }

        if (targetPixels.Length != imageByteCount)
        {
            throw new ArgumentException($"The target image must contain exactly {imageByteCount} bytes.",
                                        nameof(targetPixels));
        }

        if (sourceInvalid.Length != pixelCount)
        {
            throw new ArgumentException($"The source invalid mask must contain exactly {pixelCount} bytes.",
                                        nameof(sourceInvalid));
        }

        if (targetInvalid.Length != pixelCount)
        {
            throw new ArgumentException($"The target invalid mask must contain exactly {pixelCount} bytes.",
                                        nameof(targetInvalid));
        }

        ValidatePatchRadius(patchRadius);
    }

    private static int ValidatePatchWorksetInputs(
        ReadOnlyMemory<byte> sourcePixels, ReadOnlyMemory<byte> sourceMask,
        ReadOnlyMemory<byte> sourceGlobalMask,
        ReadOnlyMemory<byte> targetPixels, ReadOnlyMemory<byte> targetMask,
        ReadOnlyMemory<byte> targetGlobalMask,
        int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        int pixelCount;
        int imageByteCount;
        try
        {
            pixelCount     = checked(width * height);
            imageByteCount = checked(pixelCount * 3);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The supplied dimensions are too large.");
        }

        ValidateLength(sourcePixels.Length, imageByteCount, nameof(sourcePixels), "source image");
        ValidateLength(targetPixels.Length, imageByteCount, nameof(targetPixels), "target image");
        ValidateLength(sourceMask.Length, pixelCount, nameof(sourceMask), "source mask");
        ValidateLength(targetMask.Length, pixelCount, nameof(targetMask), "target mask");
        ValidateOptionalMaskLength(sourceGlobalMask.Length, pixelCount, nameof(sourceGlobalMask));
        ValidateOptionalMaskLength(targetGlobalMask.Length, pixelCount, nameof(targetGlobalMask));
        return pixelCount;
    }

    private static void ValidateLength(int actual, int expected, string parameterName, string description)
    {
        if (actual != expected)
        {
            throw new ArgumentException($"The {description} must contain exactly {expected} bytes.", parameterName);
        }
    }

    private static void ValidateOptionalMaskLength(int actual, int expected, string parameterName)
    {
        if (actual != 0 && actual != expected)
        {
            throw new ArgumentException($"The global mask must be empty or contain exactly {expected} bytes.",
                                        parameterName);
        }
    }

    private static void ValidatePatchRadius(int patchRadius)
    {
        if (patchRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patchRadius), patchRadius,
                                                  "The patch radius cannot be negative.");
        }

        if (patchRadius > MaximumPatchRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(patchRadius), patchRadius,
                "The phase-2 prototype requires the valid-pixel SSD accumulator to fit in 32 bits.");
        }
    }

    private static byte[] ExpandGlobalMask(ReadOnlyMemory<byte> globalMask, int pixelCount)
    {
        return globalMask.IsEmpty ? new byte[pixelCount] : globalMask.ToArray();
    }

    private static byte[] MergeInvalidMasks(ReadOnlyMemory<byte> mask, byte[] globalMask)
    {
        var result = new byte[mask.Length];
        var maskSpan = mask.Span;
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = maskSpan[index] != 0 || globalMask[index] != 0 ? (byte)1 : (byte)0;
        }

        return result;
    }

    private void ValidateOwnedWorkset(Direct3D12PatchWorkset workset)
    {
        ArgumentNullException.ThrowIfNull(workset);
        if (!ReferenceEquals(workset.Owner, this))
        {
            throw new ArgumentException("The workset belongs to a different Direct3D 12 prototype.",
                                        nameof(workset));
        }
    }

    private void ValidateOwnedField(Direct3D12NearestNeighborField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!ReferenceEquals(field.Owner, this))
        {
            throw new ArgumentException("The nearest-neighbor field belongs to a different Direct3D 12 prototype.",
                                        nameof(field));
        }
    }

    private void ValidateOwnedVoteBuffer(Direct3D12FixedPointVoteBuffer votes)
    {
        ArgumentNullException.ThrowIfNull(votes);
        if (!ReferenceEquals(votes.Owner, this))
        {
            throw new ArgumentException("The fixed-point vote buffer belongs to a different Direct3D 12 prototype.",
                                        nameof(votes));
        }
    }

    private void ValidateIdentityFieldPair(
        Direct3D12NearestNeighborField sourceToTarget, Direct3D12NearestNeighborField targetToSource)
    {
        ValidateOwnedField(sourceToTarget);
        ValidateOwnedField(targetToSource);
        if (!sourceToTarget.SourceToTarget || targetToSource.SourceToTarget ||
            !ReferenceEquals(sourceToTarget.Workset, targetToSource.Workset) ||
            sourceToTarget.PatchRadius != targetToSource.PatchRadius)
        {
            throw new ArgumentException(
                "Identity linking requires opposite-direction fields from the same workset and patch radius.",
                nameof(targetToSource));
        }
    }

    private static void TransitionToShaderResource(ID3D12GraphicsCommandList commandList, ID3D12Resource resource)
    {
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(resource, ResourceStates.CopyDest,
                                                                       ResourceStates.NonPixelShaderResource));
    }

    private static void CopyToReadback(
        ID3D12GraphicsCommandList commandList, ID3D12Resource resource, ID3D12Resource readback, ulong byteCount)
    {
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            resource, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource));
        commandList.CopyBufferRegion(readback, 0, resource, 0, byteCount);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            resource, ResourceStates.CopySource, ResourceStates.NonPixelShaderResource));
    }

    private static byte[] ReadBackBytes(ID3D12Resource readback, int length)
    {
        var result = new byte[length];
        readback.Map<byte>(0, length).CopyTo(result);
        readback.Unmap(0);
        return result;
    }

    private ID3D12Resource CreateUploadBuffer(ReadOnlySpan<byte> data, ulong size)
    {
        var resource = CreateBuffer(size, HeapType.Upload, ResourceStates.GenericRead, ResourceFlags.None);
        var mapped   = resource.Map<byte>(0, checked((int)size));
        mapped.Clear();
        data.CopyTo(mapped);
        resource.Unmap(0);
        return resource;
    }

    private static ulong AlignToFour(int byteCount)
    {
        return checked((ulong)((byteCount + 3L) & ~3L));
    }

    private static (int GroupCountX, int GroupCountY, int DispatchWidth) GetDispatchDimensions(int valueCount)
    {
        var groupCountX = (int)Math.Min(((long)valueCount + ShaderThreadCount - 1) / ShaderThreadCount,
                                        MaxDispatchGroupsPerDimension);
        var dispatchWidth = checked(groupCountX * ShaderThreadCount);
        var groupCountY = (int)(((long)valueCount + dispatchWidth - 1) / dispatchWidth);
        return (groupCountX, groupCountY, dispatchWidth);
    }

    private ID3D12Resource CreateBuffer(ulong size, HeapType heapType, ResourceStates initialState,
                                        ResourceFlags flags)
    {
        var heapProperties     = new HeapProperties(heapType);
        var resourceDescription = ResourceDescription.Buffer(size, flags);
        _device.CreateCommittedResource(heapProperties, HeapFlags.None, resourceDescription, initialState,
                                        out ID3D12Resource? resource).CheckError();
        return resource!;
    }

    private static IDXGIAdapter1 SelectAdapter(IDXGIFactory6 factory, bool useWarp)
    {
        if (useWarp)
        {
            return factory.EnumWarpAdapter<IDXGIAdapter1>();
        }

        for (uint index = 0;; index++)
        {
            var result = factory.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance,
                                                            out IDXGIAdapter1? adapter);
            if (result.Failure)
            {
                break;
            }

            if ((adapter!.Description1.Flags & AdapterFlags.Software) == 0 &&
                IsSupported(adapter, FeatureLevel.Level_11_0))
            {
                return adapter;
            }

            adapter.Dispose();
        }

        throw new PlatformNotSupportedException("No hardware adapter supporting Direct3D 12 was found.");
    }

    private static byte[] LoadShader(string fileName)
    {
        var assembly = typeof(Direct3D12Prototype).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                                   .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"Embedded shader '{fileName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal void ReleasePatchWorkset(Direct3D12PatchWorkset workset)
    {
        ValidateOwnedWorkset(workset);
        _gate.Wait();
        try
        {
            workset.ReleaseResources();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void ReleaseNearestNeighborField(Direct3D12NearestNeighborField field)
    {
        ValidateOwnedField(field);
        _gate.Wait();
        try
        {
            field.ReleaseResources();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void ReleaseFixedPointVoteBuffer(Direct3D12FixedPointVoteBuffer votes)
    {
        ValidateOwnedVoteBuffer(votes);
        _gate.Wait();
        try
        {
            votes.ReleaseResources();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_disposeSync)
        {
            if (_disposed)
            {
                return;
            }

            _gate.Wait();
            try
            {
                _disposed = true;
                _fence.Dispose();
                _similarityWeightsBuffer.Dispose();
                _upsampleTargetPipelineState.Dispose();
                _upsampleTargetRootSignature.Dispose();
                _downsamplePipelineState.Dispose();
                _downsampleRootSignature.Dispose();
                _unpackRgbPipelineState.Dispose();
                _unpackRgbRootSignature.Dispose();
                _fixedPointMaximizePipelineState.Dispose();
                _fixedPointMaximizeRootSignature.Dispose();
                _setIdentityLinksPipelineState.Dispose();
                _setIdentityLinksRootSignature.Dispose();
                _fixedPointVotePipelineState.Dispose();
                _fixedPointVoteRootSignature.Dispose();
                _nearestNeighborMinimizationPipelineState.Dispose();
                _nearestNeighborMinimizationRootSignature.Dispose();
                _nearestNeighborInitializationPipelineState.Dispose();
                _nearestNeighborInitializationRootSignature.Dispose();
                _patchDistancePipelineState.Dispose();
                _patchDistanceRootSignature.Dispose();
                _squareDifferencePipelineState.Dispose();
                _squareDifferenceRootSignature.Dispose();
                _commandQueue.Dispose();
                _device.Dispose();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static class FenceWaiter
    {
        internal static async Task<bool> WaitAsync(ID3D12Fence fence, ulong value)
        {
            if (fence.CompletedValue >= value)
            {
                return false;
            }

            using var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            fence.SetEventOnCompletion(value, waitHandle.SafeWaitHandle.DangerousGetHandle()).CheckError();
            await WaitHandleAsync(waitHandle).ConfigureAwait(false);
            return true;
        }

        private static Task WaitHandleAsync(WaitHandle waitHandle)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle? registration = null;
            registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                Timeout.Infinite,
                executeOnlyOnce : true);
            return CompleteAndUnregisterAsync(completion.Task, registration);
        }

        private static async Task CompleteAndUnregisterAsync(Task completion, RegisteredWaitHandle registration)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(null);
            }
        }
    }
}
