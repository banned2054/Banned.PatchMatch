using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class Direct3D12PrototypeTests
{
    [TestCase(1)]
    [TestCase(4)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(257)]
    public async Task HardwareSquareDifferencesMatchCpu(int length)
    {
        using var prototype = CreatePrototype(useWarp : false);
        var random = new Random(1212 + length);
        var left   = new byte[length];
        var right  = new byte[length];
        random.NextBytes(left);
        random.NextBytes(right);

        var actual = await prototype.SquareDifferencesAsync(left, right);

        var expected = left.Zip(right, static (a, b) =>
        {
            var difference = a - b;
            return (uint)(difference * difference);
        });
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task ReusesDeviceAcrossDispatches()
    {
        using var prototype = CreatePrototype(useWarp : false);

        var first  = await prototype.SquareDifferencesAsync(new byte[] { 0, 10 }, new byte[] { 255, 4 });
        var second = await prototype.SquareDifferencesAsync(new byte[] { 9, 2, 7 }, new byte[] { 1, 4, 7 });

        Assert.That(first, Is.EqualTo(new uint[] { 65_025, 36 }));
        Assert.That(second, Is.EqualTo(new uint[] { 64, 4, 0 }));
    }

    [Test]
    public async Task WaitsForGpuFenceWithoutOccupyingAThread()
    {
        // WARP deliberately makes the dispatch long enough to deterministically exercise the registered-wait path.
        using var prototype = CreatePrototype(useWarp : true);
        var left  = new byte[1_000_003];
        var right = new byte[left.Length];
        new Random(1212).NextBytes(left);
        new Random(3434).NextBytes(right);

        var actual = await prototype.SquareDifferencesAsync(left, right);

        Assert.That(actual, Has.Length.EqualTo(left.Length));
        Assert.That(prototype.AsynchronousFenceWaitCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task WarpSquareDifferencesMatchCpu()
    {
        using var prototype = CreatePrototype(useWarp : true);

        var actual = await prototype.SquareDifferencesAsync(new byte[] { 0, 20, 255 },
                                                            new byte[] { 255, 14, 250 });

        Assert.That(actual, Is.EqualTo(new uint[] { 65_025, 36, 25 }));
    }

    [Test]
    public async Task DispatchesAcrossD3D12XDimensionLimit()
    {
        using var prototype = CreatePrototype(useWarp : false);
        var left  = new byte[4_194_305];
        var right = new byte[left.Length];
        left[0]          = 10;
        left[4_194_303]  = 20;
        left[4_194_304]  = 30;
        right[0]         = 2;
        right[4_194_303] = 4;
        right[4_194_304] = 5;

        var actual = await prototype.SquareDifferencesAsync(left, right);

        Assert.That(actual[0], Is.EqualTo(64));
        Assert.That(actual[4_194_303], Is.EqualTo(256));
        Assert.That(actual[4_194_304], Is.EqualTo(625));
    }

    [Test]
    public void RejectsMismatchedInputs()
    {
        using var prototype = CreatePrototype(useWarp : false);

        Assert.That(async () => await prototype.SquareDifferencesAsync(new byte[1], new byte[2]),
                    Throws.ArgumentException);
    }

    [Test]
    public void HonorsCancellationBeforeSubmission()
    {
        using var prototype = CreatePrototype(useWarp : false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await prototype.SquareDifferencesAsync(new byte[16], new byte[16], cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
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
