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
