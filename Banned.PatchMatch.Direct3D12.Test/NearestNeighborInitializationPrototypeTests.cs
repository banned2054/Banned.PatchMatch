using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class NearestNeighborInitializationPrototypeTests
{
    private const int DistanceScale = 65_535;
    private const int SsdScale = 9 * 255 * 255;
    private const int MaximumAttempts = 20;

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
