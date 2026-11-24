using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class NearestNeighborInitializationPrototypeTests
{
    private const int DistanceScale = 65_535;
    private const int SsdScale = 9 * 255 * 255;
    private const int MaximumAttempts = 20;

    [TestCase(false)]
    [TestCase(true)]
    public async Task BothDirectionsMatchFrozenParallelInitializationSpecification(bool useWarp)
    {
        const int width  = 29;
        const int height = 17;
        const int radius = 2;
        const uint seed  = 0x5a17_2026;
        var random       = new Random(useWarp ? 3434 : 1212);
        var sourcePixels = RandomBytes(random, width * height * 3);
        var targetPixels = RandomBytes(random, width * height * 3);
        var sourceMask   = RandomMask(random, width * height, 0.22f);
        var targetMask   = RandomMask(random, width * height, 0.31f);
        var sourceGlobal = RandomMask(random, width * height, 0.08f);
        var targetGlobal = RandomMask(random, width * height, 0.12f);
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, sourceMask, sourceGlobal, targetPixels, targetMask, targetGlobal, width, height);

        using var sourceToTarget = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, seed, sourceToTarget : true);
        using var targetToSource = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, seed, sourceToTarget : false);
        var actualForward = await prototype.ReadNearestNeighborFieldAsync(sourceToTarget);
        var actualReverse = await prototype.ReadNearestNeighborFieldAsync(targetToSource);

        var expectedForward = InitializeCpuReference(sourcePixels, sourceMask, sourceGlobal,
                                                      targetPixels, targetMask, targetGlobal,
                                                      width, height, radius, seed);
        var expectedReverse = InitializeCpuReference(targetPixels, targetMask, targetGlobal,
                                                      sourcePixels, sourceMask, sourceGlobal,
                                                      width, height, radius, seed);
        Assert.That(actualForward, Is.EqualTo(expectedForward));
        Assert.That(actualReverse, Is.EqualTo(expectedReverse));
    }

    [Test]
    public async Task SameSeedIsDeterministicAndDifferentSeedChangesLinks()
    {
        const int width  = 37;
        const int height = 23;
        var random       = new Random(8080);
        var sourcePixels = RandomBytes(random, width * height * 3);
        var targetPixels = RandomBytes(random, width * height * 3);
        var emptyMask    = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, emptyMask, ReadOnlyMemory<byte>.Empty,
            targetPixels, emptyMask, ReadOnlyMemory<byte>.Empty, width, height);

        using var first = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 3, randomSeed : 1212, sourceToTarget : true);
        using var second = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 3, randomSeed : 1212, sourceToTarget : true);
        using var different = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 3, randomSeed : 1213, sourceToTarget : true);
        var firstSnapshot     = await prototype.ReadNearestNeighborFieldAsync(first);
        var secondSnapshot    = await prototype.ReadNearestNeighborFieldAsync(second);
        var differentSnapshot = await prototype.ReadNearestNeighborFieldAsync(different);

        Assert.That(secondSnapshot, Is.EqualTo(firstSnapshot));
        Assert.That(differentSnapshot, Is.Not.EqualTo(firstSnapshot));
    }

    [Test]
    public async Task DisposedResourcesAreRejected()
    {
        const int width = 9;
        const int height = 7;
        var pixels = new byte[width * height * 3];
        var mask = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        var field = await prototype.InitializeNearestNeighborFieldAsync(
            workset, patchRadius : 1, randomSeed : 1, sourceToTarget : true);

        field.Dispose();
        Assert.That(async () => await prototype.ReadNearestNeighborFieldAsync(field),
                    Throws.TypeOf<ObjectDisposedException>());

        workset.Dispose();
        Assert.That(async () => await prototype.InitializeNearestNeighborFieldAsync(
                        workset, patchRadius : 1, randomSeed : 1, sourceToTarget : true),
                    Throws.TypeOf<ObjectDisposedException>());
    }

    private static NearestNeighborLink[] InitializeCpuReference(
        byte[] sourcePixels, byte[] sourceMask, byte[] sourceGlobal,
        byte[] targetPixels, byte[] targetMask, byte[] targetGlobal,
        int width, int height, int radius, uint seed)
    {
        var sourceInvalid = MergeMasks(sourceMask, sourceGlobal);
        var targetInvalid = MergeMasks(targetMask, targetGlobal);
        var result = new NearestNeighborLink[width * height];
        var patchWidth = 2 * radius + 1;
        var maximumDistance = checked((uint)(SsdScale * patchWidth * patchWidth));

        for (uint sourcePixel = 0; sourcePixel < result.Length; sourcePixel++)
        {
            if (sourceGlobal[sourcePixel] != 0)
            {
                result[sourcePixel] = new NearestNeighborLink(0, 0, 0);
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

            double normalized = bestDistance;
            normalized /= SsdScale;
            var distance = (int)(DistanceScale * normalized / (patchWidth * patchWidth));
            distance = distance is < 0 or > DistanceScale ? DistanceScale : distance;
            result[sourcePixel] = new NearestNeighborLink((int)targetY, (int)targetX, distance);
        }

        return result;
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
