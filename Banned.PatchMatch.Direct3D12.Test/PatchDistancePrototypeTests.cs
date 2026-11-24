using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class PatchDistancePrototypeTests
{
    private const int DistanceScale = 65_535;
    private const int SsdScale      = 9 * 255 * 255;

    [TestCase(false)]
    [TestCase(true)]
    public async Task RandomBatchesMatchFrozenCpuSemantics(bool useWarp)
    {
        const int width  = 97;
        const int height = 53;
        var random        = new Random(useWarp ? 3434 : 1212);
        var sourcePixels  = RandomBytes(random, width * height * 3);
        var targetPixels  = RandomBytes(random, width * height * 3);
        var sourceInvalid = MergeMasks(RandomMask(random, width * height, 0.25f),
                                       RandomMask(random, width * height, 0.10f));
        var targetInvalid = MergeMasks(RandomMask(random, width * height, 0.35f),
                                       RandomMask(random, width * height, 0.15f));
        var pairs = Enumerable.Range(0, 160)
                              .Select(_ => new PatchPair(random.Next(-2, height + 2),
                                                         random.Next(-2, width + 2),
                                                         random.Next(-2, height + 2),
                                                         random.Next(-2, width + 2)))
                              .ToArray();
        using var prototype = CreatePrototype(useWarp);

        foreach (var radius in new[] { 0, 2, 3, 8 })
        {
            var actual = await prototype.CalculatePatchDistancesAsync(
                sourcePixels, sourceInvalid, targetPixels, targetInvalid, width, height, pairs, radius);
            var expected = pairs.Select(pair => CalculateCpuDistance(sourcePixels, sourceInvalid, targetPixels,
                                                                      targetInvalid, width, height, pair, radius))
                                .ToArray();

            Assert.That(actual, Is.EqualTo(expected), $"WARP={useWarp}, radius={radius}");
        }
    }

    [Test]
    public async Task SelfDistanceAndInvalidPenaltyMatchCpu()
    {
        const int width  = 33;
        const int height = 17;
        var random  = new Random(2026);
        var pixels  = RandomBytes(random, width * height * 3);
        var invalid = RandomMask(random, width * height, 0.3f);
        var pairs = new[]
        {
            new PatchPair(8, 16, 8, 16),
            new PatchPair(0, 0, 0, 0),
            new PatchPair(-2, -2, height + 1, width + 1),
            new PatchPair(4, 7, 10, 23)
        };
        using var prototype = CreatePrototype(useWarp : false);

        var actual = await prototype.CalculatePatchDistancesAsync(
            pixels, invalid, pixels, invalid, width, height, pairs, patchRadius : 3);
        var expected = pairs.Select(pair => CalculateCpuDistance(pixels, invalid, pixels, invalid,
                                                                  width, height, pair, 3));

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual[2], Is.EqualTo(DistanceScale), "a fully invalid patch must receive maximum distance");

        var allValid = new byte[width * height];
        var allValidSelf = await prototype.CalculatePatchDistancesAsync(
            pixels, allValid, pixels, allValid, width, height, new[] { pairs[0] }, patchRadius : 3);
        Assert.That(allValidSelf[0], Is.EqualTo(0),
                    "an all-valid patch compared with itself must have zero distance");
    }

    [Test]
    public async Task LargestUintAccumulatorRadiusMatchesCpu()
    {
        const int width  = 97;
        const int height = 97;
        const int radius = 42;
        var random        = new Random(42);
        var sourcePixels  = RandomBytes(random, width * height * 3);
        var targetPixels  = RandomBytes(random, width * height * 3);
        var invalid       = new byte[width * height];
        var pair          = new PatchPair(48, 48, 48, 48);
        using var prototype = CreatePrototype(useWarp : false);

        var actual = await prototype.CalculatePatchDistancesAsync(
            sourcePixels, invalid, targetPixels, invalid, width, height, new[] { pair }, radius);
        var expected = CalculateCpuDistance(sourcePixels, invalid, targetPixels, invalid,
                                             width, height, pair, radius);

        Assert.That(actual, Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void RejectsRadiusWhoseAccumulatorCanOverflow()
    {
        const int width  = 100;
        const int height = 100;
        using var prototype = CreatePrototype(useWarp : false);

        Assert.That(async () => await prototype.CalculatePatchDistancesAsync(
                        new byte[width * height * 3], new byte[width * height],
                        new byte[width * height * 3], new byte[width * height],
                        width, height, new[] { new PatchPair(50, 50, 50, 50) }, patchRadius : 43),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    private static int CalculateCpuDistance(
        byte[] sourcePixels, byte[] sourceInvalid, byte[] targetPixels, byte[] targetInvalid,
        int width, int height, PatchPair pair, int radius)
    {
        long squaredSum = 0;
        long validPixels = 0;
        for (var deltaY = -radius; deltaY <= radius; deltaY++)
        {
            var sourceY = pair.SourceY + deltaY;
            var targetY = pair.TargetY + deltaY;
            if (sourceY <= 0 || sourceY >= height - 1 || targetY <= 0 || targetY >= height - 1)
            {
                continue;
            }

            for (var deltaX = -radius; deltaX <= radius; deltaX++)
            {
                var sourceX = pair.SourceX + deltaX;
                var targetX = pair.TargetX + deltaX;
                if (sourceX <= 0 || sourceX >= width - 1 || targetX <= 0 || targetX >= width - 1)
                {
                    continue;
                }

                var sourcePixel = sourceY * width + sourceX;
                var targetPixel = targetY * width + targetX;
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

        var patchWidth  = 2L * radius + 1;
        var patchPixels = patchWidth * patchWidth;
        double distance = SsdScale * (patchPixels - validPixels) + squaredSum;
        distance /= SsdScale;
        var result = (int)(DistanceScale * distance / patchPixels);
        return result is < 0 or > DistanceScale ? DistanceScale : result;
    }

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

    private static long Square(int value) => (long)value * value;

    private static byte[] RandomBytes(Random random, int count)
    {
        var result = new byte[count];
        random.NextBytes(result);
        return result;
    }

    private static byte[] RandomMask(Random random, int count, float density)
    {
        var result = new byte[count];
        for (var index = 0; index < count; index++)
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
