using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class NearestNeighborMinimizationPrototypeTests
{
    private const int DistanceScale = 65_535;
    private const int SsdScale = 9 * 255 * 255;
    private const int MaximumAttempts = 20;

    [TestCase(false)]
    [TestCase(true)]
    public async Task BothDirectionsMatchFrozenRedBlackSpecification(bool useWarp)
    {
        const int width  = 31;
        const int height = 19;
        const int radius = 2;
        const int passes = 2;
        const uint initializationSeed = 0x5a17_2026;
        const uint minimizationSeed   = 0xc011_ab1e;
        var random       = new Random(useWarp ? 3434 : 1212);
        var sourcePixels = RandomBytes(random, width * height * 3);
        var targetPixels = RandomBytes(random, width * height * 3);
        var sourceMask   = RandomMask(random, width * height, 0.34f);
        var targetMask   = RandomMask(random, width * height, 0.27f);
        var sourceGlobal = RandomMask(random, width * height, 0.07f);
        var targetGlobal = RandomMask(random, width * height, 0.11f);
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, sourceMask, sourceGlobal, targetPixels, targetMask, targetGlobal, width, height);
        using var forward = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, initializationSeed, sourceToTarget : true);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, initializationSeed, sourceToTarget : false);

        await prototype.MinimizeNearestNeighborFieldAsync(
            forward, passes, minimizationSeed, conditionalSkip : true);
        await prototype.MinimizeNearestNeighborFieldAsync(
            reverse, passes, minimizationSeed, conditionalSkip : true);
        var actualForward = await prototype.ReadNearestNeighborFieldAsync(forward);
        var actualReverse = await prototype.ReadNearestNeighborFieldAsync(reverse);

        var expectedForward = InitializeRawField(sourcePixels, sourceMask, sourceGlobal,
                                                  targetPixels, targetMask, targetGlobal,
                                                  width, height, radius, initializationSeed);
        MinimizeRawField(expectedForward, sourcePixels, sourceMask, sourceGlobal,
                         targetPixels, targetMask, targetGlobal,
                         width, height, radius, passes, minimizationSeed,
                         sourceToTarget : true, conditionalSkip : true);
        var expectedReverse = InitializeRawField(targetPixels, targetMask, targetGlobal,
                                                  sourcePixels, sourceMask, sourceGlobal,
                                                  width, height, radius, initializationSeed);
        MinimizeRawField(expectedReverse, targetPixels, targetMask, targetGlobal,
                         sourcePixels, sourceMask, sourceGlobal,
                         width, height, radius, passes, minimizationSeed,
                         sourceToTarget : false, conditionalSkip : true);

        Assert.That(actualForward, Is.EqualTo(Normalize(expectedForward, radius)));
        Assert.That(actualReverse, Is.EqualTo(Normalize(expectedReverse, radius)));
    }

    [Test]
    public async Task ConditionalSkipLeavesIneligibleLinksUntouched()
    {
        const int width  = 23;
        const int height = 15;
        const int radius = 1;
        var random       = new Random(7070);
        var sourcePixels = RandomBytes(random, width * height * 3);
        var targetPixels = RandomBytes(random, width * height * 3);
        var sourceMask   = RandomMask(random, width * height, 0.35f);
        var emptyMask    = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, sourceMask, ReadOnlyMemory<byte>.Empty,
            targetPixels, emptyMask, ReadOnlyMemory<byte>.Empty, width, height);
        using var field = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 1212, sourceToTarget : true);
        var before = await prototype.ReadNearestNeighborFieldAsync(field);

        await prototype.MinimizeNearestNeighborFieldAsync(
            field, passCount : 2, randomSeed : 8080, conditionalSkip : true);
        var after = await prototype.ReadNearestNeighborFieldAsync(field);

        var eligibleImprovement = false;
        for (var pixel = 0; pixel < sourceMask.Length; pixel++)
        {
            if (sourceMask[pixel] == 0)
            {
                Assert.That(after[pixel], Is.EqualTo(before[pixel]), $"unmasked source pixel {pixel}");
            }
            else if (after[pixel].Distance < before[pixel].Distance)
            {
                eligibleImprovement = true;
            }
        }

        Assert.That(eligibleImprovement, Is.True);
    }

    [Test]
    public async Task ZeroPassIsNoOpAndNegativePassCountIsRejected()
    {
        const int width = 9;
        const int height = 7;
        var pixels = new byte[width * height * 3];
        var mask = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        using var field = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);
        var before = await prototype.ReadNearestNeighborFieldAsync(field);

        await prototype.MinimizeNearestNeighborFieldAsync(
            field, passCount : 0, randomSeed : 2, conditionalSkip : false);
        var after = await prototype.ReadNearestNeighborFieldAsync(field);

        Assert.That(after, Is.EqualTo(before));
        Assert.That(async () => await prototype.MinimizeNearestNeighborFieldAsync(
                        field, passCount : -1, randomSeed : 2, conditionalSkip : false),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    private static uint[] InitializeRawField(
        byte[] sourcePixels, byte[] sourceMask, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetMask, byte[] targetGlobal,
        int width, int height, int radius, uint seed)
    {
        var sourceInvalid = MergeMasks(sourceMask, sourceGlobal);
        var targetInvalid = MergeMasks(targetMask, targetGlobal);
        var result = new uint[width * height * 3];
        var patchWidth = 2 * radius + 1;
        var maximumDistance = checked((uint)(SsdScale * patchWidth * patchWidth));

        for (uint sourcePixel = 0; sourcePixel < width * height; sourcePixel++)
        {
            if (sourceGlobal[sourcePixel] != 0)
            {
                continue;
            }

            var sourceY = (int)(sourcePixel / width);
            var sourceX = (int)(sourcePixel % width);
            var bestDistance = maximumDistance;
            uint targetY = 0;
            uint targetX = 0;
            var state = MixSeed(seed ^ unchecked(sourcePixel * 0x9e37_79b9u));
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                targetY = NextRandom(ref state, (uint)height);
                targetX = NextRandom(ref state, (uint)width);
                var targetPixel = targetY * width + targetX;
                if (targetGlobal[targetPixel] != 0)
                {
                    continue;
                }

                bestDistance = CalculateRawDistance(sourcePixels, sourceInvalid, targetPixels, targetInvalid,
                                                     width, height, sourceY, sourceX,
                                                     (int)targetY, (int)targetX, radius);
                if (bestDistance < maximumDistance)
                {
                    break;
                }
            }

            var offset = checked((int)sourcePixel * 3);
            result[offset]     = targetY;
            result[offset + 1] = targetX;
            result[offset + 2] = bestDistance;
        }

        return result;
    }

    private static void MinimizeRawField(
        uint[] field,
        byte[] sourcePixels, byte[] sourceMask, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetMask, byte[] targetGlobal,
        int width, int height, int radius, int passCount, uint randomSeed,
        bool sourceToTarget, bool conditionalSkip)
    {
        var sourceInvalid = MergeMasks(sourceMask, sourceGlobal);
        var targetInvalid = MergeMasks(targetMask, targetGlobal);
        uint sweepIndex = 0;
        for (var pass = 0; pass < passCount; pass++)
        {
            MinimizeColor(field, sourcePixels, sourceInvalid, sourceGlobal,
                          targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, randomSeed, 1, 0, sweepIndex++,
                          sourceToTarget, conditionalSkip);
            MinimizeColor(field, sourcePixels, sourceInvalid, sourceGlobal,
                          targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, randomSeed, 1, 1, sweepIndex++,
                          sourceToTarget, conditionalSkip);
            MinimizeColor(field, sourcePixels, sourceInvalid, sourceGlobal,
                          targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, randomSeed, -1, 1, sweepIndex++,
                          sourceToTarget, conditionalSkip);
            MinimizeColor(field, sourcePixels, sourceInvalid, sourceGlobal,
                          targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, randomSeed, -1, 0, sweepIndex++,
                          sourceToTarget, conditionalSkip);
        }
    }

    private static void MinimizeColor(
        uint[] field,
        byte[] sourcePixels, byte[] sourceInvalid, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetInvalid, byte[] targetGlobal,
        int width, int height, int radius, uint randomSeed,
        int direction, int parity, uint sweepIndex, bool sourceToTarget, bool conditionalSkip)
    {
        for (var sourcePixel = 0; sourcePixel < width * height; sourcePixel++)
        {
            var sourceY = sourcePixel / width;
            var sourceX = sourcePixel % width;
            if (((sourceY + sourceX) & 1) != parity || sourceGlobal[sourcePixel] != 0)
            {
                continue;
            }

            if (conditionalSkip)
            {
                var sourceIsMasked = sourceInvalid[sourcePixel] != 0;
                if (sourceToTarget ? !sourceIsMasked : sourceIsMasked)
                {
                    continue;
                }
            }

            var offset = sourcePixel * 3;
            var bestTargetY = field[offset];
            var bestTargetX = field[offset + 1];
            var bestDistance = field[offset + 2];
            if (bestDistance == 0)
            {
                continue;
            }

            var neighborY = sourceY - direction;
            if (neighborY >= 0 && neighborY < height && sourceGlobal[neighborY * width + sourceX] == 0)
            {
                var neighborOffset = (neighborY * width + sourceX) * 3;
                TryUpdate(sourcePixels, sourceInvalid, targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, sourceY, sourceX,
                          checked((int)field[neighborOffset]) + direction,
                          checked((int)field[neighborOffset + 1]),
                          ref bestTargetY, ref bestTargetX, ref bestDistance);
            }

            var neighborX = sourceX - direction;
            if (neighborX >= 0 && neighborX < width && sourceGlobal[sourceY * width + neighborX] == 0)
            {
                var neighborOffset = (sourceY * width + neighborX) * 3;
                TryUpdate(sourcePixels, sourceInvalid, targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, sourceY, sourceX,
                          checked((int)field[neighborOffset]),
                          checked((int)field[neighborOffset + 1]) + direction,
                          ref bestTargetY, ref bestTargetX, ref bestDistance);
            }

            var state = MixSeed(randomSeed ^ unchecked((uint)sourcePixel * 0x9e37_79b9u) ^
                                unchecked(sweepIndex * 0x85eb_ca6bu));
            var randomScale = (Math.Min(height, width) - 1) / 2;
            while (randomScale > 0)
            {
                var diameter = (uint)(2 * randomScale + 1);
                var targetY = checked((int)bestTargetY) + (int)NextRandom(ref state, diameter) - randomScale;
                var targetX = checked((int)bestTargetX) + (int)NextRandom(ref state, diameter) - randomScale;
                targetY = Math.Clamp(targetY, 0, height - 1);
                targetX = Math.Clamp(targetX, 0, width - 1);
                TryUpdate(sourcePixels, sourceInvalid, targetPixels, targetInvalid, targetGlobal,
                          width, height, radius, sourceY, sourceX, targetY, targetX,
                          ref bestTargetY, ref bestTargetX, ref bestDistance);
                randomScale /= 2;
            }

            field[offset]     = bestTargetY;
            field[offset + 1] = bestTargetX;
            field[offset + 2] = bestDistance;
        }
    }

    private static void TryUpdate(
        byte[] sourcePixels, byte[] sourceInvalid,
        byte[] targetPixels, byte[] targetInvalid, byte[] targetGlobal,
        int width, int height, int radius, int sourceY, int sourceX, int targetY, int targetX,
        ref uint bestTargetY, ref uint bestTargetX, ref uint bestDistance)
    {
        if (targetY < 0 || targetY >= height || targetX < 0 || targetX >= width ||
            targetGlobal[targetY * width + targetX] != 0)
        {
            return;
        }

        var candidateDistance = CalculateRawDistance(
            sourcePixels, sourceInvalid, targetPixels, targetInvalid,
            width, height, sourceY, sourceX, targetY, targetX, radius);
        if (candidateDistance >= bestDistance)
        {
            return;
        }

        bestTargetY = (uint)targetY;
        bestTargetX = (uint)targetX;
        bestDistance = candidateDistance;
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

    private static NearestNeighborLink[] Normalize(uint[] rawField, int radius)
    {
        var result = new NearestNeighborLink[rawField.Length / 3];
        var patchWidth = 2L * radius + 1;
        var patchPixels = patchWidth * patchWidth;
        for (var pixel = 0; pixel < result.Length; pixel++)
        {
            var offset = pixel * 3;
            double distance = rawField[offset + 2];
            distance /= SsdScale;
            var normalized = (int)(DistanceScale * distance / patchPixels);
            normalized = normalized is < 0 or > DistanceScale ? DistanceScale : normalized;
            result[pixel] = new NearestNeighborLink(
                checked((int)rawField[offset]), checked((int)rawField[offset + 1]), normalized);
        }

        return result;
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
