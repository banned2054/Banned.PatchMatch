using Banned.PatchMatch.Direct3D12;

if (!Direct3D12Prototype.TryCreate(useWarp : false, out var prototype, out var diagnostic))
{
    throw new InvalidOperationException($"Direct3D 12 NativeAOT device creation failed: {diagnostic}");
}

using (prototype)
{
    var result = await prototype.SquareDifferencesAsync(new byte[] { 0, 20, 255, 9 },
                                                        new byte[] { 255, 14, 250, 1 });
    if (!result.AsSpan().SequenceEqual(new uint[] { 65_025, 36, 25, 64 }))
    {
        throw new InvalidOperationException("Direct3D 12 NativeAOT compute output did not match the CPU reference.");
    }

    const int width = 7;
    const int height = 7;
    var pixels = Enumerable.Range(0, width * height * 3)
                           .Select(index => (byte)((index * 37 + 11) & 0xff))
                           .ToArray();
    var invalid = new byte[width * height];
    var patchDistances = await prototype.CalculatePatchDistancesAsync(
        pixels,
        invalid,
        pixels,
        invalid,
        width,
        height,
        new[]
        {
            new PatchPair(3, 3, 3, 3),
            new PatchPair(-2, -2, height + 1, width + 1),
        },
        patchRadius : 1);
    if (!patchDistances.AsSpan().SequenceEqual(new[] { 0, 65_535 }))
    {
        throw new InvalidOperationException("Direct3D 12 NativeAOT patch-distance output did not match the CPU reference.");
    }

    var sourceGlobal = new byte[width * height];
    var globallyMaskedPixel = (height / 2) * width + width / 2;
    sourceGlobal[globallyMaskedPixel] = 1;
    using var workset = await prototype.CreatePatchWorksetAsync(
        pixels, invalid, sourceGlobal,
        pixels, invalid, ReadOnlyMemory<byte>.Empty,
        width, height);
    using var field = await prototype.InitializeNearestNeighborFieldAsync(
        workset, patchRadius : 1, randomSeed : 1212, sourceToTarget : true);
    await prototype.MinimizeNearestNeighborFieldAsync(
        field, passCount : 1, randomSeed : 8080, conditionalSkip : false);
    var links = await prototype.ReadNearestNeighborFieldAsync(field);
    if (links.Length != width * height || links[globallyMaskedPixel] != default ||
        links.Any(link => link.TargetY < 0 || link.TargetY >= height ||
                          link.TargetX < 0 || link.TargetX >= width ||
                          link.Distance < 0 || link.Distance > 65_535))
    {
        throw new InvalidOperationException("Direct3D 12 NativeAOT NNF initialization output was invalid.");
    }

    invalid[width + 1] = 1;
    var votePixels = Enumerable.Repeat((byte)77, pixels.Length).ToArray();
    using var voteWorkset = await prototype.CreatePatchWorksetAsync(
        votePixels, invalid, ReadOnlyMemory<byte>.Empty,
        votePixels, new byte[invalid.Length], ReadOnlyMemory<byte>.Empty,
        width, height);
    using var voteField = await prototype.InitializeNearestNeighborFieldAsync(
        voteWorkset, patchRadius : 1, randomSeed : 1212, sourceToTarget : false);
    using var votes = await prototype.CreateFixedPointVoteBufferAsync(voteWorkset);
    await prototype.AccumulateFixedPointVotesAsync(voteField, votes);
    var voteSnapshot = await prototype.ReadFixedPointVotesAsync(votes);
    if (!voteSnapshot.Any(vote => vote.Weight != 0))
    {
        throw new InvalidOperationException("Direct3D 12 NativeAOT fixed-point voting produced no votes.");
    }

    var reconstructed = await prototype.RunNonUpscaledIterationsAsync(
        voteWorkset, patchRadius : 1, iterationCount : 2, passCount : 1,
        initializationSeed : 1212, minimizationSeed : 8080);
    if (!reconstructed.All(value => value == 77))
    {
        throw new InvalidOperationException("Direct3D 12 NativeAOT GPU-resident iteration output was invalid.");
    }

    Console.WriteLine($"Direct3D 12 NativeAOT smoke passed on {prototype.AdapterName}.");
}
