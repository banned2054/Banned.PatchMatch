using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class NearestNeighborCoarseInitializationPrototypeTests
{
    private const int DistanceScale = 65_535;
    private const int SsdScale = 9 * 255 * 255;
    private const int MaximumAttempts = 20;

    private const int CoarseWidth  = 20;
    private const int CoarseHeight = 11;
    private const int FineWidth    = 41;
    private const int FineHeight   = 22;
    private const int Radius       = 2;
    private const uint CoarseSeed  = 0x1c0a_5e11;
    private const uint FineSeed    = 0x5a17_2026;

    [TestCase(false)]
    [TestCase(true)]
    public async Task BothDirectionsMatchIndependentReference(bool useWarp)
    {
        // 粗层 20x11，细层 41x22：宽是 2c+1，高是 2c，两种整数映射都覆盖。细层的两个半边各有一个实心孔洞块：
        // 对一个方向它是目标侧，继承来的对应点落在块内部时补丁全无效、原始距离等于上限，于是触发随机重试；
        // 对另一个方向它是源侧。细层的全局蒙版按映射关系由粗层展开，所以粗层被全局排除的源像素，
        // 其细层对应位置同样被全局排除。
        var random               = new Random(useWarp ? 3434 : 1212);
        var coarsePixels         = RandomBytes(random, CoarseWidth * CoarseHeight * 3);
        var coarseTargetPixels   = RandomBytes(random, CoarseWidth * CoarseHeight * 3);
        var finePixels           = RandomBytes(random, FineWidth * FineHeight * 3);
        var fineTargetPixels     = RandomBytes(random, FineWidth * FineHeight * 3);
        var coarseSourceMask     = RandomMask(random, CoarseWidth * CoarseHeight, 0.15f);
        var coarseTargetMask     = RandomMask(random, CoarseWidth * CoarseHeight, 0.15f);
        var coarseSourceGlobal   = RandomMask(random, CoarseWidth * CoarseHeight, 0.15f);
        var coarseTargetGlobal   = RandomMask(random, CoarseWidth * CoarseHeight, 0.15f);
        var fineSourceMask       = RandomMask(random, FineWidth * FineHeight, 0.08f);
        var fineTargetMask       = RandomMask(random, FineWidth * FineHeight, 0.08f);
        var fineSourceGlobal     = ExpandToFineLevel(coarseSourceGlobal);
        var fineTargetGlobal     = ExpandToFineLevel(coarseTargetGlobal);
        FillRectangle(fineSourceMask, FineWidth, row : 3, column : 14, rowCount : 16, columnCount : 25);
        FillRectangle(fineTargetMask, FineWidth, row : 5, column : 10, rowCount : 12, columnCount : 21);

        using var prototype = CreatePrototype(useWarp);
        using var coarseWorkset = await prototype.CreatePatchWorksetAsync(
            coarsePixels, coarseSourceMask, coarseSourceGlobal,
            coarseTargetPixels, coarseTargetMask, coarseTargetGlobal, CoarseWidth, CoarseHeight);
        using var fineWorkset = await prototype.CreatePatchWorksetAsync(
            finePixels, fineSourceMask, fineSourceGlobal,
            fineTargetPixels, fineTargetMask, fineTargetGlobal, FineWidth, FineHeight);
        using var coarseForward = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, Radius, CoarseSeed, sourceToTarget : true);
        using var coarseReverse = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, Radius, CoarseSeed, sourceToTarget : false);
        var coarseBefore = await prototype.ReadPatchWorksetAsync(coarseWorkset);
        var fineBefore   = await prototype.ReadPatchWorksetAsync(fineWorkset);

        await AssertDirectionMatchesReference(prototype, fineWorkset, coarseForward, FineSeed,
                                              finePixels, fineSourceMask, fineSourceGlobal,
                                              fineTargetPixels, fineTargetMask, fineTargetGlobal,
                                              coarseSourceGlobal, "source-to-target");
        await AssertDirectionMatchesReference(prototype, fineWorkset, coarseReverse, FineSeed,
                                              fineTargetPixels, fineTargetMask, fineTargetGlobal,
                                              finePixels, fineSourceMask, fineSourceGlobal,
                                              coarseTargetGlobal, "target-to-source");

        // 两个工作集都是只读输入。
        AssertSnapshotEquals(await prototype.ReadPatchWorksetAsync(coarseWorkset), coarseBefore, "coarse workset");
        AssertSnapshotEquals(await prototype.ReadPatchWorksetAsync(fineWorkset), fineBefore, "fine workset");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CoarseInitializationRejectsInvalidArguments(bool useWarp)
    {
        const int coarseWidth  = 8;
        const int coarseHeight = 6;
        const int fineWidth    = 16;
        const int fineHeight   = 12;
        var coarsePixels  = new byte[coarseWidth * coarseHeight * 3];
        var coarseMask    = new byte[coarseWidth * coarseHeight];
        var finePixels    = new byte[fineWidth * fineHeight * 3];
        var fineMask      = new byte[fineWidth * fineHeight];
        using var owner    = CreatePrototype(useWarp);
        using var stranger = CreatePrototype(useWarp);
        using var coarseWorkset = await owner.CreatePatchWorksetAsync(
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty,
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        using var coarseField = await owner.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var fineWorkset = await owner.CreatePatchWorksetAsync(
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);

        // 同一个工作集被拒。
        Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(coarseField, coarseWorkset, 1),
                    Throws.TypeOf<ArgumentException>());

        // 细层比粗层小被拒：宽或高任意一个小于粗层都不行。
        foreach (var (width, height) in new[] {(coarseWidth - 1, coarseHeight), (coarseWidth, coarseHeight - 1)})
        {
            using var smaller = await owner.CreatePatchWorksetAsync(
                new byte[width * height * 3], new byte[width * height], ReadOnlyMemory<byte>.Empty,
                new byte[width * height * 3], new byte[width * height], ReadOnlyMemory<byte>.Empty, width, height);
            Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(coarseField, smaller, 1),
                        Throws.TypeOf<ArgumentException>(), $"{width}x{height} must be rejected.");
        }

        // 跨 owner：粗层场或细层工作集属于另一个原型都被拒。
        using var foreignCoarseWorkset = await stranger.CreatePatchWorksetAsync(
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty,
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        using var foreignField = await stranger.InitializeNearestNeighborFieldAsync(
            foreignCoarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var foreignFineWorkset = await stranger.CreatePatchWorksetAsync(
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(foreignField, fineWorkset, 1),
                    Throws.TypeOf<ArgumentException>());
        Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(coarseField, foreignFineWorkset, 1),
                    Throws.TypeOf<ArgumentException>());

        // 已释放。
        var disposedField = await owner.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        disposedField.Dispose();
        Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(disposedField, fineWorkset, 1),
                    Throws.TypeOf<ObjectDisposedException>());

        var disposedFine = await owner.CreatePatchWorksetAsync(
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty, fineWidth, fineHeight);
        disposedFine.Dispose();
        Assert.That(async () => await owner.InitializeNearestNeighborFieldFromCoarseAsync(coarseField, disposedFine, 1),
                    Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CoarseInitializationHonorsCancellationBeforeSubmission(bool useWarp)
    {
        var coarsePixels = new byte[8 * 6 * 3];
        var coarseMask   = new byte[8 * 6];
        var finePixels   = new byte[16 * 12 * 3];
        var fineMask     = new byte[16 * 12];
        using var prototype = CreatePrototype(useWarp);
        using var coarseWorkset = await prototype.CreatePatchWorksetAsync(
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty,
            coarsePixels, coarseMask, ReadOnlyMemory<byte>.Empty, 8, 6);
        using var coarseField = await prototype.InitializeNearestNeighborFieldAsync(
            coarseWorkset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        using var fineWorkset = await prototype.CreatePatchWorksetAsync(
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty,
            finePixels, fineMask, ReadOnlyMemory<byte>.Empty, 16, 12);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await prototype.InitializeNearestNeighborFieldFromCoarseAsync(
                coarseField, fineWorkset, 1, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static async Task AssertDirectionMatchesReference(
        Direct3D12Prototype prototype, Direct3D12PatchWorkset fineWorkset,
        Direct3D12NearestNeighborField coarseField, uint fineSeed,
        byte[] sourcePixels, byte[] sourceMask, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetMask, byte[] targetGlobal,
        byte[] coarseSourceGlobal, string direction)
    {
        var coarseWidth  = coarseField.Workset.Width;
        var coarseHeight = coarseField.Workset.Height;
        var fineWidth    = fineWorkset.Width;
        var fineHeight   = fineWorkset.Height;
        var coarseLinks  = await prototype.ReadNearestNeighborFieldAsync(coarseField);
        var expected     = InitializeFromCoarseCpuReference(
            sourcePixels, sourceMask, sourceGlobal, targetPixels, targetMask, targetGlobal,
            coarseLinks, coarseWidth, coarseHeight, fineWidth, fineHeight, coarseField.PatchRadius, fineSeed);

        using var fineField = await prototype.InitializeNearestNeighborFieldFromCoarseAsync(
            coarseField, fineWorkset, fineSeed);
        var actual = await prototype.ReadNearestNeighborFieldAsync(fineField);

        Assert.That((fineField.SourceToTarget, fineField.PatchRadius, fineField.Workset),
                    Is.EqualTo((coarseField.SourceToTarget, coarseField.PatchRadius, fineWorkset)),
                    $"{direction}: the new field must inherit direction and patch radius.");
        Assert.That(await prototype.ReadNearestNeighborFieldAsync(coarseField), Is.EqualTo(coarseLinks),
                    $"{direction}: coarse field (read-only input)");
        Assert.That(actual, Is.EqualTo(expected.Links), $"{direction}: links");

        // 区分力：结果里既有直接继承粗层链接的像素，也有被随机重试改写的像素；还有重试途中先算出等于上限的
        // 距离、之后才被接受的像素（场里必须保留最后算出的那个距离）。粗层被全局排除的源像素，新场对应位置是 (0, 0, 0)。
        var inherited               = 0;
        var rewritten               = 0;
        var acceptedAfterRejection  = 0;
        var excludedCoarsePixels    = new bool[coarseWidth * coarseHeight];
        for (var pixel = 0; pixel < actual.Length; pixel++)
        {
            var coarsePixel = (pixel / fineWidth) * coarseHeight / fineHeight * coarseWidth
                            + (pixel % fineWidth) * coarseWidth / fineWidth;
            var coarseLink  = coarseLinks[coarsePixel];
            var mappedX     = coarseLink.TargetX * fineWidth / coarseWidth;
            var mappedY     = coarseLink.TargetY * fineHeight / coarseHeight;
            if (coarseSourceGlobal[coarsePixel] != 0)
            {
                excludedCoarsePixels[coarsePixel] = true;
                Assert.That(actual[pixel], Is.EqualTo(new NearestNeighborLink(0, 0, 0)),
                            $"{direction}: fine pixel {pixel} maps to a globally excluded coarse pixel.");
                continue;
            }

            if (!expected.Retried[pixel])
            {
                inherited++;
                Assert.That((actual[pixel].TargetX, actual[pixel].TargetY), Is.EqualTo((mappedX, mappedY)),
                            $"{direction}: fine pixel {pixel} must keep the inherited coarse link.");
            }
            else
            {
                rewritten += actual[pixel].TargetX != mappedX || actual[pixel].TargetY != mappedY ? 1 : 0;
                acceptedAfterRejection += expected.ComputedDistances[pixel] >= 2 && actual[pixel].Distance < DistanceScale ? 1 : 0;
            }
        }

        Assert.That(inherited, Is.GreaterThan(0), $"{direction}: the data must contain inherited links.");
        Assert.That(rewritten, Is.GreaterThan(0), $"{direction}: the data must contain links rewritten by a random retry.");
        Assert.That(acceptedAfterRejection, Is.GreaterThan(0),
                    $"{direction}: the data must contain a retry accepted after a rejected attempt.");
        Assert.That(excludedCoarsePixels.Count(value => value), Is.GreaterThan(0),
                    $"{direction}: the data must contain globally excluded coarse pixels.");
    }

    private readonly record struct CoarseInitializationReference(
        NearestNeighborLink[] Links, bool[] Retried, int[] ComputedDistances);

    /// <summary>
    /// 独立 CPU 参考：按 NearestNeighborField(source, target, metric, random, previous) 的语义，源像素被全局排除时写入
    /// (0, 0, 0)；否则按 InitializeFieldFrom 的 double 缩放取粗层链接并映射到细层，在细层图上计算原始距离；
    /// 原始距离达到上限时按 RandomizeLink(reset: false) 做至多 20 次随机重试，每线程的随机流与并行随机初始化相同，
    /// 并且总是保留最后算出的距离。Retried 标记哪些像素进入了重试，ComputedDistances 是其中真正算过距离的次数。
    /// 与 GPU shader 相互独立。
    /// </summary>
    private static CoarseInitializationReference InitializeFromCoarseCpuReference(
        byte[] sourcePixels, byte[] sourceMask, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetMask, byte[] targetGlobal,
        NearestNeighborLink[] previous, int previousWidth, int previousHeight,
        int width, int height, int radius, uint seed)
    {
        var sourceInvalid = MergeMasks(sourceMask, sourceGlobal);
        var targetInvalid = MergeMasks(targetMask, targetGlobal);
        var links             = new NearestNeighborLink[width * height];
        var retried           = new bool[links.Length];
        var computedDistances = new int[links.Length];
        var patchWidth        = 2 * radius + 1;
        var maximumDistance   = checked((uint)(SsdScale * patchWidth * patchWidth));
        var verticalScale     = (double)height / previousHeight;
        var horizontalScale   = (double)width  / previousWidth;

        for (uint sourcePixel = 0; sourcePixel < links.Length; sourcePixel++)
        {
            if (sourceGlobal[sourcePixel] != 0)
            {
                links[sourcePixel] = new NearestNeighborLink(0, 0, 0);
                continue;
            }

            var sourceY   = (int)(sourcePixel / width);
            var sourceX   = (int)(sourcePixel % width);
            var previousY = (int)Math.Min(sourceY / verticalScale, previousHeight - 1d);
            var previousX = (int)Math.Min(sourceX / horizontalScale, previousWidth - 1d);
            var inherited = previous[previousY * previousWidth + previousX];
            var targetY   = (int)(inherited.TargetY * verticalScale);
            var targetX   = (int)(inherited.TargetX * horizontalScale);
            var raw = CalculateRawDistance(sourcePixels, sourceInvalid, targetPixels, targetInvalid,
                                           width, height, sourceY, sourceX, targetY, targetX, radius);
            if (raw >= maximumDistance)
            {
                retried[sourcePixel] = true;
                var state = MixSeed(seed ^ unchecked(sourcePixel * 0x9e37_79b9u));
                for (var attempt = 0; attempt < MaximumAttempts; attempt++)
                {
                    targetY = (int)NextRandom(ref state, (uint)height);
                    targetX = (int)NextRandom(ref state, (uint)width);
                    if (targetGlobal[targetY * width + targetX] != 0)
                    {
                        continue;
                    }

                    computedDistances[sourcePixel]++;
                    raw = CalculateRawDistance(sourcePixels, sourceInvalid, targetPixels, targetInvalid,
                                               width, height, sourceY, sourceX, targetY, targetX, radius);
                    if (raw < maximumDistance)
                    {
                        break;
                    }
                }
            }

            double normalized = raw;
            normalized /= SsdScale;
            var distance = (int)(DistanceScale * normalized / (patchWidth * patchWidth));
            distance = distance is < 0 or > DistanceScale ? DistanceScale : distance;
            links[sourcePixel] = new NearestNeighborLink(targetY, targetX, distance);
        }

        return new CoarseInitializationReference(links, retried, computedDistances);
    }

    private static uint CalculateRawDistance(
        byte[] sourcePixels, byte[] sourceInvalid, byte[] targetPixels, byte[] targetInvalid,
        int width, int height, int sourceY, int sourceX, int targetY, int targetX, int radius)
    {
        uint squaredSum = 0;
        uint validPixels = 0;
        for (var deltaY = -radius; deltaY <= radius; deltaY++)
        {
            var sourcePatchY = sourceY + deltaY;
            var targetPatchY = targetY + deltaY;
            if (sourcePatchY <= 0 || sourcePatchY >= height - 1 ||
                targetPatchY <= 0 || targetPatchY >= height - 1)
            {
                continue;
            }

            for (var deltaX = -radius; deltaX <= radius; deltaX++)
            {
                var sourcePatchX = sourceX + deltaX;
                var targetPatchX = targetX + deltaX;
                if (sourcePatchX <= 0 || sourcePatchX >= width - 1 ||
                    targetPatchX <= 0 || targetPatchX >= width - 1)
                {
                    continue;
                }

                var sourcePixel = sourcePatchY * width + sourcePatchX;
                var targetPixel = targetPatchY * width + targetPatchX;
                if (sourceInvalid[sourcePixel] != 0 || targetInvalid[targetPixel] != 0)
                {
                    continue;
                }

                var sourceOffset = sourcePixel * 3;
                var targetOffset = targetPixel * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    squaredSum += Square(sourcePixels[sourceOffset + channel] - targetPixels[targetOffset + channel]);
                    squaredSum += Square(GradientX(sourcePixels, sourceOffset, channel) -
                                         GradientX(targetPixels, targetOffset, channel));
                    squaredSum += Square(GradientY(sourcePixels, sourceOffset, channel, width) -
                                         GradientY(targetPixels, targetOffset, channel, width));
                }

                validPixels++;
            }
        }

        var patchWidth = 2u * (uint)radius + 1u;
        return checked((uint)SsdScale * (patchWidth * patchWidth - validPixels) + squaredSum);
    }

    private static uint MixSeed(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7feb_352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846c_a68bu);
        value ^= value >> 16;
        return value;
    }

    private static uint NextRandom(ref uint state, uint maxExclusive)
    {
        state = unchecked(state * 214_013u + 2_531_011u);
        return ((state >> 16) & 0x7fffu) % maxExclusive;
    }

    private static uint Square(int value) => checked((uint)(value * value));

    private static int GradientX(byte[] pixels, int pixelOffset, int channel)
    {
        return unchecked((byte)((pixels[pixelOffset + 3 + channel] / 2) -
                                (pixels[pixelOffset - 3 + channel] / 2) + 128));
    }

    private static int GradientY(byte[] pixels, int pixelOffset, int channel, int width)
    {
        var rowStride = width * 3;
        return unchecked((byte)((pixels[pixelOffset + rowStride + channel] / 2) -
                                (pixels[pixelOffset - rowStride + channel] / 2) + 128));
    }

    // 按细层像素到粗层像素的映射（x * 粗层宽 / 细层宽，整数除）展开全局蒙版。
    private static byte[] ExpandToFineLevel(byte[] coarseGlobal)
    {
        var result = new byte[FineWidth * FineHeight];
        for (var y = 0; y < FineHeight; y++)
        {
            for (var x = 0; x < FineWidth; x++)
            {
                result[y * FineWidth + x] = coarseGlobal[(y * CoarseHeight / FineHeight) * CoarseWidth
                                                         + x * CoarseWidth / FineWidth];
            }
        }

        return result;
    }

    private static void AssertSnapshotEquals(PatchWorksetSnapshot actual, PatchWorksetSnapshot expected, string context)
    {
        AssertBufferEquals(actual.SourcePixels,  expected.SourcePixels,  context, "SourcePixels");
        AssertBufferEquals(actual.SourceMask,    expected.SourceMask,    context, "SourceMask");
        AssertBufferEquals(actual.SourceInvalid, expected.SourceInvalid, context, "SourceInvalid");
        AssertBufferEquals(actual.SourceGlobal,  expected.SourceGlobal,  context, "SourceGlobal");
        AssertBufferEquals(actual.TargetPixels,  expected.TargetPixels,  context, "TargetPixels");
        AssertBufferEquals(actual.TargetInvalid, expected.TargetInvalid, context, "TargetInvalid");
        AssertBufferEquals(actual.TargetGlobal,  expected.TargetGlobal,  context, "TargetGlobal");
    }

    private static void AssertBufferEquals(byte[] actual, byte[] expected, string context, string name)
    {
        if (actual.AsSpan().SequenceEqual(expected))
        {
            return;
        }

        Assert.That(actual, Is.EqualTo(expected), $"{context}: {name}");
    }

    private static void FillRectangle(
        byte[] mask, int imageWidth, int row, int column, int rowCount, int columnCount, byte value = 1)
    {
        for (var y = row; y < row + rowCount; y++)
        {
            for (var x = column; x < column + columnCount; x++)
            {
                mask[y * imageWidth + x] = value;
            }
        }
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var result = new byte[count];
        random.NextBytes(result);
        return result;
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

    private static byte[] MergeMasks(byte[] mask, byte[] globalMask)
    {
        var result = new byte[mask.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = mask[index] != 0 || globalMask[index] != 0 ? (byte)1 : (byte)0;
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
