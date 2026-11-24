using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class FixedPointVotePrototypeTests
{
    private const int DistanceScale = 65_535;

    [TestCase(false)]
    [TestCase(true)]
    public async Task BidirectionalVotesMatchIndependentFixedPointReference(bool useWarp)
    {
        const int width  = 27;
        const int height = 19;
        const int radius = 2;
        var random       = new Random(useWarp ? 4545 : 2323);
        var sourcePixels = Enumerable.Repeat((byte)90, width * height * 3).ToArray();
        var targetPixels = Enumerable.Repeat((byte)97, width * height * 3).ToArray();
        var sourceMask   = RandomMask(random, width * height, 0.025f);
        var targetMask   = RandomMask(random, width * height, 0.01f);
        var sourceGlobal = RandomMask(random, width * height, 0.01f);
        var targetGlobal = RandomMask(random, width * height, 0.015f);
        sourceMask[(height / 2) * width + width / 2] = 1;
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, sourceMask, sourceGlobal,
            targetPixels, targetMask, targetGlobal, width, height);
        using var forward = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 1212, sourceToTarget : true);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 3434, sourceToTarget : false);
        await prototype.MinimizeNearestNeighborFieldAsync(
            forward, passCount : 1, randomSeed : 5656, conditionalSkip : true);
        await prototype.MinimizeNearestNeighborFieldAsync(
            reverse, passCount : 1, randomSeed : 7878, conditionalSkip : true);
        var forwardLinks = await prototype.ReadNearestNeighborFieldAsync(forward);
        var reverseLinks = await prototype.ReadNearestNeighborFieldAsync(reverse);
        using var votes = await prototype.CreateFixedPointVoteBufferAsync(workset);

        await prototype.AccumulateFixedPointVotesAsync(forward, votes);
        await prototype.AccumulateFixedPointVotesAsync(reverse, votes);
        var actual = await prototype.ReadFixedPointVotesAsync(votes);
        var maximized = await prototype.MaximizeFixedPointVotesAsync(votes);

        var expected = new FixedPointVote[width * height];
        AccumulateCpuReference(expected, forwardLinks, sourceToTarget : true,
                               sourcePixels, sourceMask, sourceGlobal, targetMask, targetGlobal,
                               width, height, radius);
        AccumulateCpuReference(expected, reverseLinks, sourceToTarget : false,
                               sourcePixels, sourceMask, sourceGlobal, targetMask, targetGlobal,
                               width, height, radius);
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual.Any(vote => vote.Weight != 0), Is.True);
        Assert.That(maximized, Is.EqualTo(MaximizeCpuReference(expected, targetPixels, targetGlobal)));
        Assert.That(maximized, Is.Not.EqualTo(targetPixels));
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    public async Task UpscaledVotesMatchIndependentFixedPointReference(
        bool useWarp, bool oddWidth, bool oddHeight)
    {
        const int radius = 2;
        var coarseWidth = oddWidth ? 13 : 12;
        var coarseHeight = oddHeight ? 9 : 8;
        var fineWidth = 2 * coarseWidth + (oddWidth ? 1 : 0);
        var fineHeight = 2 * coarseHeight + (oddHeight ? 1 : 0);
        var seed = 100 + (useWarp ? 50 : 0) + (oddWidth ? 1 : 0) + (oddHeight ? 2 : 0);
        var random = new Random(seed);
        var coarseSourcePixels = PatternPixels(coarseWidth, coarseHeight, 60, 40);
        var coarseTargetPixels = NoisyPixels(coarseSourcePixels, random, 5);
        var fineSourcePixels = PatternPixels(fineWidth, fineHeight, 180, 50);
        var fineTargetPixels = Enumerable.Repeat((byte)33, fineWidth * fineHeight * 3).ToArray();
        // 粗层孔洞取确定性隔离位置：5x5 邻域内双侧蒙版清空，孔洞中心 patch 的无效像素数恰为 1，
        // 使最小化后的链接距离落在相似度表非零区间，正向场必有非零权重投票者。
        // 位置必须完全处于梯度边界内（距离内核跳过最外一圈像素），且两个孔洞彼此相距超过 patch 半径。
        var coarseSourceMask = new byte[coarseWidth * coarseHeight];
        var coarseSourceGlobal = RandomMask(random, coarseWidth * coarseHeight, 0.02f);
        var coarseTargetMask = new byte[coarseWidth * coarseHeight];
        var coarseTargetGlobal = RandomMask(random, coarseWidth * coarseHeight, 0.02f);
        foreach (var (holeY, holeX) in new[] {(3, 3), (4, 6)})
        {
            for (var deltaY = -2; deltaY <= 2; deltaY++)
            {
                for (var deltaX = -2; deltaX <= 2; deltaX++)
                {
                    var y = holeY + deltaY;
                    var x = holeX + deltaX;
                    if (y < 0 || y >= coarseHeight || x < 0 || x >= coarseWidth)
                    {
                        continue;
                    }

                    var neighbor = y * coarseWidth + x;
                    coarseSourceMask[neighbor] = 0;
                    coarseSourceGlobal[neighbor] = 0;
                    coarseTargetMask[neighbor] = 0;
                    coarseTargetGlobal[neighbor] = 0;
                }
            }

            coarseSourceMask[holeY * coarseWidth + holeX] = 1;
        }
        // 细层蒙版与粗层相互独立，用于证明投票读取的是细层有效性而不是粗层有效性。
        var fineSourceMask = RandomMask(random, fineWidth * fineHeight, 0.05f);
        var fineSourceGlobal = RandomMask(random, fineWidth * fineHeight, 0.02f);
        var fineTargetMask = new byte[fineWidth * fineHeight];
        var fineTargetGlobal = RandomMask(random, fineWidth * fineHeight, 0.02f);
        coarseTargetGlobal[0] = 1;
        using var prototype = CreatePrototype(useWarp);
        using var coarseWorkset = await prototype.CreatePatchWorksetAsync(
            coarseSourcePixels, coarseSourceMask, coarseSourceGlobal,
            coarseTargetPixels, coarseTargetMask, coarseTargetGlobal, coarseWidth, coarseHeight);
        using var fineWorkset = await prototype.CreatePatchWorksetAsync(
            fineSourcePixels, fineSourceMask, fineSourceGlobal,
            fineTargetPixels, fineTargetMask, fineTargetGlobal, fineWidth, fineHeight);
        using var forward = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, radius, randomSeed : 1717, sourceToTarget : true);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, radius, randomSeed : 2727, sourceToTarget : false);
        await prototype.MinimizeNearestNeighborFieldAsync(
            forward, passCount : 1, randomSeed : 3737, conditionalSkip : true);
        await prototype.MinimizeNearestNeighborFieldAsync(
            reverse, passCount : 1, randomSeed : 4747, conditionalSkip : true);
        var forwardLinks = await prototype.ReadNearestNeighborFieldAsync(forward);
        var reverseLinks = await prototype.ReadNearestNeighborFieldAsync(reverse);
        using var forwardVotes = await prototype.CreateFixedPointVoteBufferAsync(fineWorkset);
        using var reverseVotes = await prototype.CreateFixedPointVoteBufferAsync(fineWorkset);
        using var combinedVotes = await prototype.CreateFixedPointVoteBufferAsync(fineWorkset);

        await prototype.AccumulateUpscaledFixedPointVotesAsync(forward, forwardVotes);
        await prototype.AccumulateUpscaledFixedPointVotesAsync(reverse, reverseVotes);
        await prototype.AccumulateUpscaledFixedPointVotesAsync(forward, combinedVotes);
        await prototype.AccumulateUpscaledFixedPointVotesAsync(reverse, combinedVotes);
        var actualForward = await prototype.ReadFixedPointVotesAsync(forwardVotes);
        var actualReverse = await prototype.ReadFixedPointVotesAsync(reverseVotes);
        var actualCombined = await prototype.ReadFixedPointVotesAsync(combinedVotes);
        var maximizedForward = await prototype.MaximizeFixedPointVotesAsync(forwardVotes);
        var maximizedCombined = await prototype.MaximizeFixedPointVotesAsync(combinedVotes);

        var expectedForward = new FixedPointVote[fineWidth * fineHeight];
        AccumulateUpscaledCpuReference(expectedForward, forwardLinks, sourceToTarget : true,
                                       fineSourcePixels, fineSourceMask, fineSourceGlobal,
                                       coarseSourceMask, coarseSourceGlobal, coarseTargetGlobal,
                                       coarseWidth, coarseHeight, fineWidth, fineHeight, radius);
        var expectedReverse = new FixedPointVote[fineWidth * fineHeight];
        AccumulateUpscaledCpuReference(expectedReverse, reverseLinks, sourceToTarget : false,
                                       fineSourcePixels, fineSourceMask, fineSourceGlobal,
                                       coarseTargetMask, coarseTargetGlobal, coarseSourceGlobal,
                                       coarseWidth, coarseHeight, fineWidth, fineHeight, radius);
        var expectedCombined = (FixedPointVote[])expectedForward.Clone();
        AccumulateUpscaledCpuReference(expectedCombined, reverseLinks, sourceToTarget : false,
                                       fineSourcePixels, fineSourceMask, fineSourceGlobal,
                                       coarseTargetMask, coarseTargetGlobal, coarseSourceGlobal,
                                       coarseWidth, coarseHeight, fineWidth, fineHeight, radius);

        Assert.That(actualForward, Is.EqualTo(expectedForward));
        Assert.That(actualReverse, Is.EqualTo(expectedReverse));
        Assert.That(actualCombined, Is.EqualTo(expectedCombined));
        Assert.That(actualForward.Any(vote => vote.Weight != 0), Is.True);
        Assert.That(actualReverse.Any(vote => vote.Weight != 0), Is.True);
        Assert.That(maximizedForward,
                    Is.EqualTo(MaximizeCpuReference(expectedForward, fineTargetPixels, fineTargetGlobal)));
        Assert.That(maximizedCombined,
                    Is.EqualTo(MaximizeCpuReference(expectedCombined, fineTargetPixels, fineTargetGlobal)));
        Assert.That(maximizedCombined, Is.Not.EqualTo(fineTargetPixels));
        Assert.That(fineTargetGlobal.Any(mask => mask != 0), Is.True);

        // 粗层目标 (0,0) 被全局排除：其细层子像素不接收任何方向的票，必须保留初值。
        foreach (var pixel in new[] {0, 1, fineWidth, fineWidth + 1})
        {
            Assert.That(actualCombined[pixel].Weight, Is.EqualTo(0ul));
            AssertNoVotePixelKeepsInitial(maximizedCombined, pixel);
        }

        // 细层尺寸为 2c+1 时，末行/末列没有粗层对应点，不能有票，也不能被改写。
        if (oddHeight)
        {
            var lastRowStart = (fineHeight - 1) * fineWidth;
            Assert.That(actualCombined.Skip(lastRowStart).Take(fineWidth),
                        Is.All.Matches<FixedPointVote>(vote => vote.Weight == 0));
            for (var column = 0; column < fineWidth; column++)
            {
                AssertNoVotePixelKeepsInitial(maximizedCombined, lastRowStart + column);
            }
        }

        if (oddWidth)
        {
            for (var row = 0; row < fineHeight; row++)
            {
                Assert.That(actualCombined[row * fineWidth + fineWidth - 1].Weight, Is.EqualTo(0ul));
                AssertNoVotePixelKeepsInitial(maximizedCombined, row * fineWidth + fineWidth - 1);
            }
        }
    }

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

    private static void AccumulateCpuReference(
        FixedPointVote[] votes, NearestNeighborLink[] links, bool sourceToTarget,
        byte[] originalPixels, byte[] originalMask, byte[] originalGlobal,
        byte[] targetMask, byte[] targetGlobal,
        int width, int height, int radius)
    {
        var weights = BuildSimilarityWeights();
        var fieldSourceMask = sourceToTarget ? originalMask : targetMask;
        var fieldSourceGlobal = sourceToTarget ? originalGlobal : targetGlobal;
        var fieldTargetGlobal = sourceToTarget ? targetGlobal : originalGlobal;
        for (var fieldSourcePixel = 0; fieldSourcePixel < links.Length; fieldSourcePixel++)
        {
            if (fieldSourceGlobal[fieldSourcePixel] != 0)
            {
                continue;
            }

            var fieldSourceIsMasked = fieldSourceMask[fieldSourcePixel] != 0;
            if (sourceToTarget ? !fieldSourceIsMasked : fieldSourceIsMasked)
            {
                continue;
            }

            var sourceCenterY = fieldSourcePixel / width;
            var sourceCenterX = fieldSourcePixel % width;
            var link = links[fieldSourcePixel];
            var weight = weights[Math.Clamp(link.Distance, 0, DistanceScale)];
            for (var deltaY = -radius; deltaY <= radius; deltaY++)
            {
                var fieldSourceY = sourceCenterY + deltaY;
                var fieldTargetY = link.TargetY + deltaY;
                if (fieldSourceY < 0 || fieldSourceY >= height ||
                    fieldTargetY < 0 || fieldTargetY >= height)
                {
                    continue;
                }

                for (var deltaX = -radius; deltaX <= radius; deltaX++)
                {
                    var fieldSourceX = sourceCenterX + deltaX;
                    var fieldTargetX = link.TargetX + deltaX;
                    if (fieldSourceX < 0 || fieldSourceX >= width ||
                        fieldTargetX < 0 || fieldTargetX >= width)
                    {
                        continue;
                    }

                    var fieldSourceSample = fieldSourceY * width + fieldSourceX;
                    var fieldTargetSample = fieldTargetY * width + fieldTargetX;
                    if (fieldSourceGlobal[fieldSourceSample] != 0 ||
                        fieldTargetGlobal[fieldTargetSample] != 0)
                    {
                        continue;
                    }

                    var originalSourceSample = sourceToTarget ? fieldSourceSample : fieldTargetSample;
                    var destinationSample = sourceToTarget ? fieldTargetSample : fieldSourceSample;
                    if (originalMask[originalSourceSample] != 0 || originalGlobal[originalSourceSample] != 0)
                    {
                        continue;
                    }

                    var sourceOffset = originalSourceSample * 3;
                    var previous = votes[destinationSample];
                    votes[destinationSample] = new FixedPointVote(
                        checked(previous.Red + originalPixels[sourceOffset] * (ulong)weight),
                        checked(previous.Green + originalPixels[sourceOffset + 1] * (ulong)weight),
                        checked(previous.Blue + originalPixels[sourceOffset + 2] * (ulong)weight),
                        checked(previous.Weight + weight));
                }
            }
        }
    }

    /// <summary>
    /// 独立 CPU 参考实现：按 CPU 引擎的 VoteForLink/IsValidCorrespondence/CopyVote/WeightedCopy 语义，
    /// 把粗层最近邻场展开为 2x2 细层投票。与 GPU shader 相互独立，仅共享同一算法约定。
    /// </summary>
    private static void AccumulateUpscaledCpuReference(
        FixedPointVote[] votes, NearestNeighborLink[] links, bool sourceToTarget,
        byte[] fineSourcePixels, byte[] fineSourceMask, byte[] fineSourceGlobal,
        byte[] coarseFieldSourceMask, byte[] coarseFieldSourceGlobal, byte[] coarseFieldTargetGlobal,
        int coarseWidth, int coarseHeight, int fineWidth, int fineHeight, int radius)
    {
        var weights = BuildSimilarityWeights();
        for (var coarsePixel = 0; coarsePixel < links.Length; coarsePixel++)
        {
            if (coarseFieldSourceGlobal[coarsePixel] != 0)
            {
                continue;
            }

            var fieldSourceIsMasked = coarseFieldSourceMask[coarsePixel] != 0;
            if (sourceToTarget ? !fieldSourceIsMasked : fieldSourceIsMasked)
            {
                continue;
            }

            var sourceCenterY = coarsePixel / coarseWidth;
            var sourceCenterX = coarsePixel % coarseWidth;
            var link = links[coarsePixel];
            var weight = weights[Math.Clamp(link.Distance, 0, DistanceScale)];
            for (var deltaY = -radius; deltaY <= radius; deltaY++)
            {
                var fieldSourceY = sourceCenterY + deltaY;
                var fieldTargetY = link.TargetY + deltaY;
                if (fieldSourceY < 0 || fieldSourceY >= coarseHeight ||
                    fieldTargetY < 0 || fieldTargetY >= coarseHeight)
                {
                    continue;
                }

                for (var deltaX = -radius; deltaX <= radius; deltaX++)
                {
                    var fieldSourceX = sourceCenterX + deltaX;
                    var fieldTargetX = link.TargetX + deltaX;
                    if (fieldSourceX < 0 || fieldSourceX >= coarseWidth ||
                        fieldTargetX < 0 || fieldTargetX >= coarseWidth)
                    {
                        continue;
                    }

                    var fieldSourceSample = fieldSourceY * coarseWidth + fieldSourceX;
                    var fieldTargetSample = fieldTargetY * coarseWidth + fieldTargetX;
                    if (coarseFieldSourceGlobal[fieldSourceSample] != 0 ||
                        coarseFieldTargetGlobal[fieldTargetSample] != 0)
                    {
                        continue;
                    }

                    var coarseSourceSample = sourceToTarget ? fieldSourceSample : fieldTargetSample;
                    var coarseTargetSample = sourceToTarget ? fieldTargetSample : fieldSourceSample;
                    var sourceY = coarseSourceSample / coarseWidth;
                    var sourceX = coarseSourceSample % coarseWidth;
                    var targetY = coarseTargetSample / coarseWidth;
                    var targetX = coarseTargetSample % coarseWidth;
                    for (var upsampleY = 0; upsampleY < 2; upsampleY++)
                    {
                        for (var upsampleX = 0; upsampleX < 2; upsampleX++)
                        {
                            var fineSourceY = 2 * sourceY + upsampleY;
                            var fineSourceX = 2 * sourceX + upsampleX;
                            var fineTargetY = 2 * targetY + upsampleY;
                            var fineTargetX = 2 * targetX + upsampleX;
                            if (fineSourceY >= fineHeight || fineSourceX >= fineWidth ||
                                fineTargetY >= fineHeight || fineTargetX >= fineWidth)
                            {
                                continue;
                            }

                            var fineSourcePixel = fineSourceY * fineWidth + fineSourceX;
                            if (fineSourceMask[fineSourcePixel] != 0 || fineSourceGlobal[fineSourcePixel] != 0)
                            {
                                continue;
                            }

                            var sourceOffset = fineSourcePixel * 3;
                            var fineTargetPixel = fineTargetY * fineWidth + fineTargetX;
                            var previous = votes[fineTargetPixel];
                            votes[fineTargetPixel] = new FixedPointVote(
                                checked(previous.Red + fineSourcePixels[sourceOffset] * (ulong)weight),
                                checked(previous.Green + fineSourcePixels[sourceOffset + 1] * (ulong)weight),
                                checked(previous.Blue + fineSourcePixels[sourceOffset + 2] * (ulong)weight),
                                checked(previous.Weight + weight));
                        }
                    }
                }
            }
        }
    }

    private static void AssertNoVotePixelKeepsInitial(byte[] maximized, int pixel)
    {
        var offset = pixel * 3;
        Assert.That(maximized[offset], Is.EqualTo((byte)33), $"pixel {pixel} red");
        Assert.That(maximized[offset + 1], Is.EqualTo((byte)33), $"pixel {pixel} green");
        Assert.That(maximized[offset + 2], Is.EqualTo((byte)33), $"pixel {pixel} blue");
    }

    private static byte[] PatternPixels(int width, int height, int basis, int amplitude)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(basis + ((x * 7 + y * 13) % amplitude));
                var offset = (y * width + x) * 3;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }

        return pixels;
    }

    private static byte[] NoisyPixels(byte[] pixels, Random random, int amplitude)
    {
        var result = (byte[])pixels.Clone();
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)Math.Clamp(result[index] + random.Next(-amplitude, amplitude + 1), 0, 255);
        }

        return result;
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

    private static byte[] MaximizeCpuReference(
        FixedPointVote[] votes, byte[] targetPixels, byte[] targetGlobal)
    {
        var result = (byte[])targetPixels.Clone();
        for (var pixel = 0; pixel < votes.Length; pixel++)
        {
            var vote = votes[pixel];
            if (targetGlobal[pixel] != 0 || vote.Weight == 0)
            {
                continue;
            }

            var offset = pixel * 3;
            result[offset] = RoundToByte(vote.Red, vote.Weight);
            result[offset + 1] = RoundToByte(vote.Green, vote.Weight);
            result[offset + 2] = RoundToByte(vote.Blue, vote.Weight);
        }

        return result;
    }

    private static byte RoundToByte(ulong weightedChannel, ulong weight)
    {
        return checked((byte)Math.Clamp(
            Math.Round((double)weightedChannel / weight, MidpointRounding.ToEven), byte.MinValue, byte.MaxValue));
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
