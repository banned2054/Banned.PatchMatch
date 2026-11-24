using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class IdentityLinksPrototypeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BothDirectionsMatchSourceMaskNeighborhoodSpecification(bool useWarp)
    {
        const int width = 25;
        const int height = 17;
        const int radius = 2;
        var random = new Random(useWarp ? 6161 : 5151);
        var pixels = RandomBytes(random, width * height * 3);
        var sourceMask = RandomMask(random, width * height, 0.04f);
        var sourceGlobal = RandomMask(random, width * height, 0.03f);
        var emptyMask = new byte[width * height];
        sourceMask[(height / 2) * width + width / 2] = 1;
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, sourceMask, sourceGlobal,
            pixels, emptyMask, ReadOnlyMemory<byte>.Empty, width, height);
        using var forward = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 1212, sourceToTarget : true);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 3434, sourceToTarget : false);
        var forwardBefore = await prototype.ReadNearestNeighborFieldAsync(forward);
        var reverseBefore = await prototype.ReadNearestNeighborFieldAsync(reverse);

        await prototype.SetIdentityLinksAsync(forward, reverse);
        var forwardAfter = await prototype.ReadNearestNeighborFieldAsync(forward);
        var reverseAfter = await prototype.ReadNearestNeighborFieldAsync(reverse);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var y = pixel / width;
            var x = pixel % width;
            if (ContainsMask(sourceMask, sourceGlobal, width, height, y, x, radius))
            {
                Assert.That(forwardAfter[pixel], Is.EqualTo(forwardBefore[pixel]));
                Assert.That(reverseAfter[pixel], Is.EqualTo(reverseBefore[pixel]));
            }
            else
            {
                var identity = new NearestNeighborLink(y, x, 0);
                Assert.That(forwardAfter[pixel], Is.EqualTo(identity));
                Assert.That(reverseAfter[pixel], Is.EqualTo(identity));
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NonUpscaledIterationPreservesImageWithoutHoles(bool useWarp)
    {
        const int width = 21;
        const int height = 15;
        const int radius = 2;
        var pixels = RandomBytes(new Random(useWarp ? 9292 : 8181), width * height * 3);
        var emptyMask = new byte[width * height];
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, emptyMask, ReadOnlyMemory<byte>.Empty,
            pixels, emptyMask, ReadOnlyMemory<byte>.Empty, width, height);

        var result = await prototype.RunNonUpscaledIterationAsync(
            workset, radius, passCount : 2, initializationSeed : 1212, minimizationSeed : 3434);

        Assert.That(result, Is.EqualTo(pixels));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NonUpscaledIterationFillsSolidColorHole(bool useWarp)
    {
        const int width = 25;
        const int height = 19;
        const int radius = 2;
        const byte background = 60;
        var pixels = Enumerable.Repeat(background, width * height * 3).ToArray();
        var mask = new byte[width * height];
        for (var y = height / 2 - 1; y <= height / 2 + 1; y++)
        {
            for (var x = width / 2 - 1; x <= width / 2 + 1; x++)
            {
                var pixel = y * width + x;
                mask[pixel] = 1;
                pixels[pixel * 3] = byte.MaxValue;
                pixels[pixel * 3 + 1] = byte.MaxValue;
                pixels[pixel * 3 + 2] = byte.MaxValue;
            }
        }

        var targetMask = new byte[mask.Length];
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, targetMask, ReadOnlyMemory<byte>.Empty, width, height);

        var result = await prototype.RunNonUpscaledIterationAsync(
            workset, radius, passCount : 3, initializationSeed : 1212, minimizationSeed : 3434);

        Assert.That(result, Is.EqualTo(Enumerable.Repeat(background, result.Length).ToArray()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InPlaceMaximizationMatchesStandaloneReadback(bool useWarp)
    {
        const int width = 19;
        const int height = 13;
        const int radius = 2;
        var random = new Random(useWarp ? 14_141 : 13_131);
        var pixels = RandomBytes(random, width * height * 3);
        var sourceMask = RandomMask(random, width * height, 0.08f);
        var targetMask = new byte[sourceMask.Length];
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, sourceMask, ReadOnlyMemory<byte>.Empty,
            pixels, targetMask, ReadOnlyMemory<byte>.Empty, width, height);
        using var forward = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 1212, sourceToTarget : true);
        using var reverse = await prototype.InitializeNearestNeighborFieldAsync(
            workset, radius, randomSeed : 3434, sourceToTarget : false);
        await prototype.SetIdentityLinksAsync(forward, reverse);
        await prototype.MinimizeNearestNeighborFieldAsync(
            forward, passCount : 1, randomSeed : 5656, conditionalSkip : true);
        await prototype.MinimizeNearestNeighborFieldAsync(
            reverse, passCount : 1, randomSeed : 7878, conditionalSkip : true);
        using var votes = await prototype.CreateFixedPointVoteBufferAsync(workset);
        await prototype.AccumulateFixedPointVotesAsync(forward, votes);
        await prototype.AccumulateFixedPointVotesAsync(reverse, votes);
        var expected = await prototype.MaximizeFixedPointVotesAsync(votes);

        await prototype.MaximizeFixedPointVotesInPlaceAsync(votes);
        var actual = await prototype.ReadTargetPixelsAsync(workset);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MultipleNonUpscaledIterationsAreDeterministicAndConsumeUpdatedTarget(bool useWarp)
    {
        const int width = 25;
        const int height = 19;
        const int radius = 2;
        var random = new Random(15_151);
        var pixels = RandomBytes(random, width * height * 3);
        var mask = new byte[width * height];
        for (var y = 7; y < 12; y++)
        {
            for (var x = 9; x < 16; x++)
            {
                var pixel = y * width + x;
                mask[pixel] = 1;
                pixels[pixel * 3] = byte.MaxValue;
                pixels[pixel * 3 + 1] = byte.MaxValue;
                pixels[pixel * 3 + 2] = byte.MaxValue;
            }
        }

        async Task<byte[]> RunAsync(int iterationCount)
        {
            using var prototype = CreatePrototype(useWarp);
            using var workset = await prototype.CreatePatchWorksetAsync(
                pixels, mask, ReadOnlyMemory<byte>.Empty,
                pixels, new byte[mask.Length], ReadOnlyMemory<byte>.Empty, width, height);
            return await prototype.RunNonUpscaledIterationsAsync(
                workset, radius, iterationCount, passCount : 2,
                initializationSeed : 1212, minimizationSeed : 3434);
        }

        var firstIteration = await RunAsync(iterationCount : 1);
        var firstRun = await RunAsync(iterationCount : 3);
        var secondRun = await RunAsync(iterationCount : 3);

        Assert.That(firstRun, Is.EqualTo(secondRun));
        Assert.That(firstRun, Is.Not.EqualTo(firstIteration));
    }

    [Test]
    public async Task MultipleNonUpscaledIterationsValidateCountAndCancellation()
    {
        const int width = 9;
        const int height = 7;
        var pixels = new byte[width * height * 3];
        var mask = new byte[width * height];
        using var prototype = CreatePrototype(useWarp : false);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty,
            pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await prototype.RunNonUpscaledIterationsAsync(
                        workset, patchRadius : 1, iterationCount : 0, passCount : 1,
                        initializationSeed : 1, minimizationSeed : 2),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(async () => await prototype.RunNonUpscaledIterationsAsync(
                        workset, patchRadius : 1, iterationCount : 1, passCount : 1,
                        initializationSeed : 1, minimizationSeed : 2, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
    }

    private static bool ContainsMask(
        byte[] mask, byte[] globalMask, int width, int height, int centerY, int centerX, int radius)
    {
        for (var deltaY = -radius; deltaY <= radius; deltaY++)
        {
            for (var deltaX = -radius; deltaX <= radius; deltaX++)
            {
                var y = centerY + deltaY;
                var x = centerX + deltaX;
                if (y >= 0 && y < height && x >= 0 && x < width)
                {
                    var pixel = y * width + x;
                    if (mask[pixel] != 0 && globalMask[pixel] == 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
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
