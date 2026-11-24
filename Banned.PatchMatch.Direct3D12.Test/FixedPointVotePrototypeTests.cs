using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class FixedPointVotePrototypeTests
{
    private const int DistanceScale = 65_535;

    [Test]
    public async Task UpscaledVoteRejectsIllegalAdjacentSizes()
    {
        const int coarseWidth = 12;
        const int coarseHeight = 8;
        var pixels = new byte[coarseWidth * coarseHeight * 3];
        var mask = new byte[coarseWidth * coarseHeight];
        mask[coarseWidth + 1] = 1;
        using var prototype = CreatePrototype(useWarp : false);
        using var coarseWorkset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, new byte[mask.Length], ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        using var field = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var sameWorksetVotes = await prototype.CreateFixedPointVoteBufferAsync(coarseWorkset);

        Assert.That(async () => await prototype.AccumulateUpscaledFixedPointVotesAsync(field, sameWorksetVotes),
                    Throws.TypeOf<ArgumentException>());

        // 逐一隔离非法维度：高度必须落在 [2c, 2c+1]，宽度同理。
        foreach (var (fineWidth, fineHeight) in new[] {(2 * coarseWidth - 1, 2 * coarseHeight + 1),
                                                      (2 * coarseWidth + 2, 2 * coarseHeight + 1),
                                                      (2 * coarseWidth,     2 * coarseHeight - 1)})
        {
            var finePixels = new byte[fineWidth * fineHeight * 3];
            using var illegalWorkset = await prototype.CreatePatchWorksetAsync(
                finePixels, new byte[fineWidth * fineHeight], ReadOnlyMemory<byte>.Empty,
                finePixels, new byte[fineWidth * fineHeight], ReadOnlyMemory<byte>.Empty,
                fineWidth, fineHeight);
            using var illegalVotes = await prototype.CreateFixedPointVoteBufferAsync(illegalWorkset);
            Assert.That(async () => await prototype.AccumulateUpscaledFixedPointVotesAsync(field, illegalVotes),
                        Throws.TypeOf<ArgumentException>());
        }
    }

    [Test]
    public async Task UpscaledVoteRejectsForeignOwnerAndDisposedResources()
    {
        const int coarseWidth = 12;
        const int coarseHeight = 8;
        const int fineWidth = 24;
        const int fineHeight = 16;
        var coarsePixels = new byte[coarseWidth * coarseHeight * 3];
        var coarseMask = new byte[coarseWidth * coarseHeight];
        coarseMask[coarseWidth + 1] = 1;
        var finePixels = new byte[fineWidth * fineHeight * 3];
        var emptyFineMask = new byte[fineWidth * fineHeight];
        using var owner = CreatePrototype(useWarp : false);
        using var stranger = CreatePrototype(useWarp : false);
        using var coarseWorkset = await owner.CreatePatchWorksetAsync(
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty,
            coarsePixels, new byte[coarseMask.Length], ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        using var field = await owner.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var fineWorkset = await stranger.CreatePatchWorksetAsync(
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        var foreignVotes = await stranger.CreateFixedPointVoteBufferAsync(fineWorkset);

        Assert.That(async () => await owner.AccumulateUpscaledFixedPointVotesAsync(field, foreignVotes),
                    Throws.TypeOf<ArgumentException>());
        foreignVotes.Dispose();
        stranger.Dispose();

        using var ownFineWorkset = await owner.CreatePatchWorksetAsync(
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        var ownVotes = await owner.CreateFixedPointVoteBufferAsync(ownFineWorkset);

        ownFineWorkset.Dispose();
        Assert.That(async () => await owner.AccumulateUpscaledFixedPointVotesAsync(field, ownVotes),
                    Throws.TypeOf<ObjectDisposedException>());

        ownVotes.Dispose();
        Assert.That(async () => await owner.AccumulateUpscaledFixedPointVotesAsync(field, ownVotes),
                    Throws.TypeOf<ObjectDisposedException>());

        // 粗层工作集被释放而 field 与合法细层 votes 仍存活：field.ThrowIfDisposed 检查其 workset。
        var liveFineWorkset = await owner.CreatePatchWorksetAsync(
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, emptyFineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        var liveVotes = await owner.CreateFixedPointVoteBufferAsync(liveFineWorkset);
        coarseWorkset.Dispose();
        Assert.That(async () => await owner.AccumulateUpscaledFixedPointVotesAsync(field, liveVotes),
                    Throws.TypeOf<ObjectDisposedException>());
        liveVotes.Dispose();
        liveFineWorkset.Dispose();
    }

    [Test]
    public async Task UpscaledVoteHonorsCancellationBeforeSubmission()
    {
        const int coarseWidth = 12;
        const int coarseHeight = 8;
        const int fineWidth = 24;
        var coarsePixels = new byte[coarseWidth * coarseHeight * 3];
        var coarseMask = new byte[coarseWidth * coarseHeight];
        coarseMask[coarseWidth + 1] = 1;
        const int fineHeight = 16;
        var finePixels = new byte[fineWidth * fineHeight * 3];
        using var prototype = CreatePrototype(useWarp : false);
        using var coarseWorkset = await prototype.CreatePatchWorksetAsync(
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty,
            coarsePixels, new byte[coarseMask.Length], ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        using var field = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var fineWorkset = await prototype.CreatePatchWorksetAsync(
            finePixels, new byte[fineWidth * fineHeight], ReadOnlyMemory<byte>.Empty,
            finePixels, new byte[fineWidth * fineHeight], ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        using var votes = await prototype.CreateFixedPointVoteBufferAsync(fineWorkset);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await prototype.AccumulateUpscaledFixedPointVotesAsync(field, votes, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
        var untouched = await prototype.ReadFixedPointVotesAsync(votes);
        Assert.That(untouched, Is.All.Matches<FixedPointVote>(vote =>
            vote.Red == 0 && vote.Green == 0 && vote.Blue == 0 && vote.Weight == 0));
    }

    [Test]
    public void UpscaledWorstCaseAccumulatorStaysWithin64Bits()
    {
        // 原型把 patch 半径限制在 42：单个细层像素在每个方向上最多被 (2r+1)^2 个粗层对应展开覆盖。
        const int maximumPatchRadius = 42;
        var patchOffsets = (ulong)(2 * maximumPatchRadius + 1) * (2 * maximumPatchRadius + 1);
        var worstCasePerPixel = 2ul * patchOffsets * byte.MaxValue *
                                Direct3D12FixedPointVoteBuffer.WeightScale;
        var worstCaseLowLaneAddition = byte.MaxValue * (ulong)Direct3D12FixedPointVoteBuffer.WeightScale;
        Assert.That(worstCasePerPixel, Is.LessThan(ulong.MaxValue));
        Assert.That(worstCaseLowLaneAddition, Is.LessThanOrEqualTo(uint.MaxValue));
        Assert.That(worstCasePerPixel * 4096ul * 4096ul, Is.LessThan(ulong.MaxValue));
    }

    [Test]
    public void SplitAtomicLayoutCoversWorstCase4KSum()
    {
        const int pixelCount4K = 4096 * 4096;
        var upperBound = Direct3D12Prototype.GetWorstCaseFixedPointChannelSum(pixelCount4K, fieldCount : 2);
        var maximumDimensionsBound =
            Direct3D12Prototype.GetWorstCaseFixedPointChannelSum(int.MaxValue, fieldCount : 2);

        Assert.That(upperBound, Is.GreaterThan(uint.MaxValue));
        Assert.That(upperBound, Is.LessThan(ulong.MaxValue));
        Assert.That(maximumDimensionsBound, Is.LessThan(ulong.MaxValue));
        Assert.That(upperBound,
                    Is.EqualTo((ulong)pixelCount4K * 2 * byte.MaxValue *
                               Direct3D12FixedPointVoteBuffer.WeightScale));
    }

    [Test]
    public async Task AccumulationCarriesAcrossLowAtomicLane()
    {
        const int width = 17;
        const int height = 17;
        const int radius = 2;
        var pixels = Enumerable.Repeat(byte.MaxValue, width * height * 3).ToArray();
        var emptyMask = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, emptyMask, ReadOnlyMemory<byte>.Empty,
            pixels, emptyMask, ReadOnlyMemory<byte>.Empty, width, height);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 9191, sourceToTarget : false);
        await prototype.MinimizeNearestNeighborFieldAsync(
            reverse, passCount : 1, randomSeed : 8282, conditionalSkip : true);
        using var votes = await prototype.CreateFixedPointVoteBufferAsync(workset);

        await prototype.AccumulateFixedPointVotesAsync(reverse, votes);
        var single = await prototype.ReadFixedPointVotesAsync(votes);
        var maximumSingleChannel = single.Max(vote => vote.Red);
        Assert.That(maximumSingleChannel, Is.GreaterThan(0));
        var repetitionCount = checked((int)(uint.MaxValue / maximumSingleChannel) + 1);
        Assert.That(repetitionCount, Is.LessThanOrEqualTo(1024));
        for (var repetition = 1; repetition < repetitionCount; repetition++)
        {
            await prototype.AccumulateFixedPointVotesAsync(reverse, votes);
        }

        var accumulated = await prototype.ReadFixedPointVotesAsync(votes);
        Assert.That(accumulated.Any(vote => vote.Red > uint.MaxValue), Is.True);
        for (var pixel = 0; pixel < accumulated.Length; pixel++)
        {
            Assert.That(accumulated[pixel], Is.EqualTo(new FixedPointVote(
                checked(single[pixel].Red * (ulong)repetitionCount),
                checked(single[pixel].Green * (ulong)repetitionCount),
                checked(single[pixel].Blue * (ulong)repetitionCount),
                checked(single[pixel].Weight * (ulong)repetitionCount))));
        }
    }

    [Test]
    public async Task DisposedVoteBufferIsRejected()
    {
        const int width = 9;
        const int height = 7;
        var pixels = new byte[width * height * 3];
        var mask = new byte[width * height];
        mask[width + 1] = 1;
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, new byte[mask.Length], ReadOnlyMemory<byte>.Empty, width, height);
        using var field = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        var votes = await prototype.CreateFixedPointVoteBufferAsync(workset);

        votes.Dispose();

        Assert.That(async () => await prototype.AccumulateFixedPointVotesAsync(field, votes),
                    Throws.TypeOf<ObjectDisposedException>());
        Assert.That(async () => await prototype.ReadFixedPointVotesAsync(votes),
                    Throws.TypeOf<ObjectDisposedException>());
    }

    private static byte[] RandomMask(Random random, int count, float density)
    {
        var result = new byte[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = random.NextSingle() < density ? (byte)1 : (byte)0;
        }

        return result;
    }

    private static Direct3D12Prototype CreatePrototype(bool useWarp)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Direct3D 12 tests only run on Windows.");
        }

        if (!Direct3D12Prototype.TryCreate(useWarp, out var prototype, out var diagnostic))
        {
            Assert.Ignore($"Direct3D 12 prototype is unavailable: {diagnostic}");
        }

        TestContext.Progress.WriteLine($"Adapter: {prototype.AdapterName}; WARP={prototype.IsWarp}");
        return prototype;
    }
}
