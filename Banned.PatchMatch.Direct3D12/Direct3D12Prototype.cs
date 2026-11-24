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
