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
    private readonly ID3D12RootSignature         _setIdentityLinksRootSignature;
    private readonly ID3D12PipelineState         _setIdentityLinksPipelineState;
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

        var setIdentityLinksShader = LoadShader("SetIdentityLinks.dxil");
        _setIdentityLinksRootSignature = _device.CreateRootSignature(setIdentityLinksShader);
        _setIdentityLinksPipelineState = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _setIdentityLinksRootSignature,
            ComputeShader = setIdentityLinksShader
        });

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
                _setIdentityLinksPipelineState.Dispose();
                _setIdentityLinksRootSignature.Dispose();
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
