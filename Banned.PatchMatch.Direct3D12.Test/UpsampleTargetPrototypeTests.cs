using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class UpsampleTargetPrototypeTests
{
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public async Task UpsampleTargetMatchesIndependentReference(bool useWarp, bool oddWidth, bool oddHeight)
    {
        // 三层链 A → B → C。宽、高各自按 2c 或 2c + 1 取相邻尺寸，C 与 B 的奇偶相反，所以四种组合各出现两次：
        // (2c+1, 2c+1) → (2c, 2c)、(2c, 2c) → (2c+1, 2c+1)、(2c+1, 2c) → (2c, 2c+1)、(2c, 2c+1) → (2c+1, 2c)。
        var coarseWidth  = oddWidth  ? 33 : 32;
        var coarseHeight = oddHeight ? 25 : 24;
        var fineWidth    = 2 * coarseWidth  + (oddWidth  ? 1 : 0);
        var fineHeight   = 2 * coarseHeight + (oddHeight ? 1 : 0);
        var finerWidth   = 2 * fineWidth     + (oddWidth  ? 0 : 1);
        var finerHeight  = 2 * fineHeight    + (oddHeight ? 0 : 1);
        var random       = new Random(1000 + (useWarp ? 50 : 0) + (oddWidth ? 1 : 0) + (oddHeight ? 2 : 0));
        var coarse       = CreateLevel(random, coarseWidth, coarseHeight);
        var fine         = CreateLevel(random, fineWidth, fineHeight);
        var finer        = CreateLevel(random, finerWidth, finerHeight);
        // 细层 source global 与粗层 target global、与细层 target global 的初值都不同：细层 target global
        // 若错误地沿用了其中任何一个，逐字节比较都会失败。
        Assert.That(fine.SourceGlobal, Is.Not.EqualTo(coarse.TargetGlobal));
        Assert.That(fine.SourceGlobal, Is.Not.EqualTo(fine.TargetGlobal));

        var expectedFine = UpsampleTargetCpuReference(coarse, coarse.TargetPixels, coarse.TargetMask,
                                                      coarse.TargetGlobal, fine);
        // 金字塔里除最粗层外，每次上采样的输入都是上一次上采样的结果。CPU 语义下它的孔洞蒙版是粗层导出的 mask、
        // 全局蒙版已换成本层 source 的 global，两者独立保存；GPU 工作集只存合并后的 invalid，第二次上采样要能从
        // "invalid | global" 还原出同一个孔洞集合。
        var expectedFiner = UpsampleTargetCpuReference(
            fine, expectedFine.Pixels, expectedFine.Hole, expectedFine.Global, finer);

        using var prototype = CreatePrototype(useWarp);
        using var coarseWorkset = await CreateWorksetAsync(prototype, coarse);
        using var fineWorkset   = await CreateWorksetAsync(prototype, fine);
        using var finerWorkset  = await CreateWorksetAsync(prototype, finer);
        var coarseBefore = await prototype.ReadPatchWorksetAsync(coarseWorkset);
        var fineBefore   = await prototype.ReadPatchWorksetAsync(fineWorkset);
        // 细层 target 半边的初值与期望结果的三个缓冲都不同，逐字节相等才能证明缓冲确实被改写。
        Assert.That(fineBefore.TargetPixels,  Is.Not.EqualTo(expectedFine.Pixels));
        Assert.That(fineBefore.TargetInvalid, Is.Not.EqualTo(expectedFine.Invalid));
        Assert.That(fineBefore.TargetGlobal,  Is.Not.EqualTo(expectedFine.Global));

        await prototype.UpsampleTargetAsync(coarseWorkset, fineWorkset);

        var coarseAfter = await prototype.ReadPatchWorksetAsync(coarseWorkset);
        var actualFine  = await prototype.ReadPatchWorksetAsync(fineWorkset);
        AssertSnapshotEquals(coarseAfter, coarseBefore, "coarse workset (read-only input)");
        AssertSnapshotEquals(actualFine, SnapshotOf(fine, expectedFine),
                             $"{coarse.Width}x{coarse.Height} -> {fine.Width}x{fine.Height}");
        AssertHoleAndGlobalMaskDriveDifferentOutputs(actualFine, expectedFine, coarse.TargetPixels, coarse, fine,
                                                     "first upsample");

        await prototype.UpsampleTargetAsync(fineWorkset, finerWorkset);

        var fineAfter   = await prototype.ReadPatchWorksetAsync(fineWorkset);
        var actualFiner = await prototype.ReadPatchWorksetAsync(finerWorkset);
        AssertSnapshotEquals(fineAfter, actualFine, "fine workset after the second upsample");
        AssertSnapshotEquals(actualFiner, SnapshotOf(finer, expectedFiner),
                             $"{fine.Width}x{fine.Height} -> {finer.Width}x{finer.Height}");
        AssertHoleAndGlobalMaskDriveDifferentOutputs(actualFiner, expectedFiner, expectedFine.Pixels, fine, finer,
                                                     "second upsample");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpsampleTargetRejectsNonAdjacentSizes(bool useWarp)
    {
        const int coarseWidth  = 12;
        const int coarseHeight = 8;
        using var prototype = CreatePrototype(useWarp);
        using var coarse = await CreateWorksetAsync(prototype, CreateLevel(new Random(11), coarseWidth, coarseHeight));

        // 逐一隔离非法维度：宽度必须落在 [2c, 2c+1]，高度同理。与粗层同尺寸也不算相邻层。
        foreach (var (fineWidth, fineHeight) in new[] {(coarseWidth, coarseHeight),
                                                      (2 * coarseWidth - 1, 2 * coarseHeight + 1),
                                                      (2 * coarseWidth + 2, 2 * coarseHeight),
                                                      (2 * coarseWidth, 2 * coarseHeight - 1),
                                                      (2 * coarseWidth + 1, 2 * coarseHeight + 2)})
        {
            using var illegal = await CreateWorksetAsync(prototype, CreateLevel(new Random(12), fineWidth, fineHeight));
            Assert.That(async () => await prototype.UpsampleTargetAsync(coarse, illegal),
                        Throws.TypeOf<ArgumentException>(), $"{fineWidth}x{fineHeight} must be rejected.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpsampleTargetRejectsForeignOwnerAndDisposedWorksets(bool useWarp)
    {
        var coarseLevel = CreateLevel(new Random(13), 12, 8);
        var fineLevel   = CreateLevel(new Random(14), 25, 16);
        using var owner    = CreatePrototype(useWarp);
        using var stranger = CreatePrototype(useWarp);
        using var ownCoarse     = await CreateWorksetAsync(owner, coarseLevel);
        using var ownFine       = await CreateWorksetAsync(owner, fineLevel);
        using var foreignCoarse = await CreateWorksetAsync(stranger, coarseLevel);
        using var foreignFine   = await CreateWorksetAsync(stranger, fineLevel);

        Assert.That(async () => await owner.UpsampleTargetAsync(foreignCoarse, ownFine),
                    Throws.TypeOf<ArgumentException>());
        Assert.That(async () => await owner.UpsampleTargetAsync(ownCoarse, foreignFine),
                    Throws.TypeOf<ArgumentException>());

        var disposedCoarse = await CreateWorksetAsync(owner, coarseLevel);
        disposedCoarse.Dispose();
        Assert.That(async () => await owner.UpsampleTargetAsync(disposedCoarse, ownFine),
                    Throws.TypeOf<ObjectDisposedException>());

        var disposedFine = await CreateWorksetAsync(owner, fineLevel);
        disposedFine.Dispose();
        Assert.That(async () => await owner.UpsampleTargetAsync(ownCoarse, disposedFine),
                    Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpsampleTargetHonorsCancellationBeforeSubmission(bool useWarp)
    {
        var coarseLevel = CreateLevel(new Random(15), 12, 8);
        var fineLevel   = CreateLevel(new Random(16), 24, 16);
        using var prototype = CreatePrototype(useWarp);
        using var coarse = await CreateWorksetAsync(prototype, coarseLevel);
        using var fine   = await CreateWorksetAsync(prototype, fineLevel);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await prototype.UpsampleTargetAsync(coarse, fine, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
        // 取消后细层 target 半边仍是上传时的内容。
        var untouched = await prototype.ReadPatchWorksetAsync(fine);
        AssertSnapshotEquals(untouched, SnapshotOf(fineLevel,
                                                   fineLevel.TargetPixels,
                                                   MergeMasks(fineLevel.TargetMask, fineLevel.TargetGlobal),
                                                   fineLevel.TargetGlobal),
                             "fine workset after cancellation");
    }

    private readonly record struct Level(
        int Width, int Height,
        byte[] SourcePixels, byte[] SourceMask, byte[] SourceGlobal,
        byte[] TargetPixels, byte[] TargetMask, byte[] TargetGlobal);

    private readonly record struct Target(byte[] Pixels, byte[] Hole, byte[] Invalid, byte[] Global);

    /// <summary>
    /// 独立 CPU 参考：严格按 MaskedImage.Upsample 的写法。细层像素取粗层的对应像素（sourceY = y * 粗层高 / 细层高，
    /// sourceX = x * 粗层宽 / 细层宽，整数除）。先算粗层导出的孔洞蒙版 mask：粗层该点被全局排除或为孔洞时细层为孔洞、
    /// 像素保持 0，否则复制像素；再用细层 source 的全局蒙版整体替换 global；工作集只保存合并后的 invalid，
    /// 所以最后 invalid = mask | global。粗层的孔洞蒙版与全局蒙版是两个独立数组。与 GPU shader 相互独立。
    /// </summary>
    private static Target UpsampleTargetCpuReference(
        Level coarse, byte[] coarseTargetPixels, byte[] coarseMask, byte[] coarseGlobal, Level fine)
    {
        var finePixelCount = fine.Width * fine.Height;
        var pixels = new byte[finePixelCount * 3];
        var mask   = new byte[finePixelCount];
        var global = new byte[finePixelCount];
        for (var y = 0; y < fine.Height; y++)
        {
            for (var x = 0; x < fine.Width; x++)
            {
                var sourceY     = y * coarse.Height / fine.Height;
                var sourceX     = x * coarse.Width / fine.Width;
                var coarsePixel = sourceY * coarse.Width + sourceX;
                var finePixel   = y * fine.Width + x;
                if (coarseGlobal[coarsePixel] != 0)
                {
                    global[finePixel] = 1;
                    mask[finePixel]   = 1;
                    continue;
                }

                if (coarseMask[coarsePixel] != 0)
                {
                    mask[finePixel] = 1;
                    continue;
                }

                var coarseOffset = coarsePixel * 3;
                var fineOffset   = finePixel * 3;
                pixels[fineOffset]     = coarseTargetPixels[coarseOffset];
                pixels[fineOffset + 1] = coarseTargetPixels[coarseOffset + 1];
                pixels[fineOffset + 2] = coarseTargetPixels[coarseOffset + 2];
            }
        }

        // Upsample 的最后一步：newGlobalMask 非空时整体替换细层 target 的全局蒙版。
        Array.Copy(fine.SourceGlobal, global, finePixelCount);
        // 工作集只保存合并后的 invalid：孔洞 | 全局。
        return new Target(pixels, mask, MergeMasks(mask, global), global);
    }

    // 有区分力的检查，直接看 GPU 结果：
    // - "仅细层全局排除"（细层 source global 为 1、粗层对应点有效）：invalid 为 1，颜色照常复制。抓住"漏并细层 global"
    //   和"用细层 global 去清颜色"。
    // - "仅粗层孔洞"（粗层导出为孔洞、细层 source global 为 0）：invalid 为 1，颜色为 0。抓住"粗层孔洞不进颜色"。
    private static void AssertHoleAndGlobalMaskDriveDifferentOutputs(
        PatchWorksetSnapshot actual, Target expected, byte[] coarseTargetPixels, Level coarse, Level fine,
        string context)
    {
        var fineGlobalOnly     = 0;
        var copiedNonZeroColor = 0;
        var coarseHoleOnly     = 0;
        for (var y = 0; y < fine.Height; y++)
        {
            for (var x = 0; x < fine.Width; x++)
            {
                var pixel        = y * fine.Width + x;
                var offset       = pixel * 3;
                var coarseOffset = (y * coarse.Height / fine.Height * coarse.Width + x * coarse.Width / fine.Width) * 3;
                var actualColor  = (actual.TargetPixels[offset], actual.TargetPixels[offset + 1],
                                    actual.TargetPixels[offset + 2]);
                if (expected.Hole[pixel] == 0)
                {
                    if (fine.SourceGlobal[pixel] == 0)
                    {
                        continue;
                    }

                    fineGlobalOnly++;
                    var coarseColor = (coarseTargetPixels[coarseOffset], coarseTargetPixels[coarseOffset + 1],
                                       coarseTargetPixels[coarseOffset + 2]);
                    Assert.That(actual.TargetInvalid[pixel], Is.EqualTo(1),
                                $"{context}: pixel ({x}, {y}) is excluded by the fine global mask and must be invalid.");
                    Assert.That(actualColor, Is.EqualTo(coarseColor),
                                $"{context}: pixel ({x}, {y}) must keep the copied coarse color.");
                    copiedNonZeroColor += (coarseColor.Item1 | coarseColor.Item2 | coarseColor.Item3) != 0 ? 1 : 0;
                }
                else if (fine.SourceGlobal[pixel] == 0)
                {
                    coarseHoleOnly++;
                    Assert.That(actual.TargetInvalid[pixel], Is.EqualTo(1),
                                $"{context}: pixel ({x}, {y}) is a coarse hole and must be invalid.");
                    Assert.That(actualColor, Is.EqualTo(((byte)0, (byte)0, (byte)0)),
                                $"{context}: pixel ({x}, {y}) is a coarse hole and must keep the color 0.");
                }
            }
        }

        Assert.That(fineGlobalOnly, Is.GreaterThan(0),
                    $"{context}: the data must contain pixels excluded only by the fine global mask.");
        Assert.That(copiedNonZeroColor, Is.GreaterThan(0), $"{context}: the copied colors must not be all zero.");
        Assert.That(coarseHoleOnly, Is.GreaterThan(0),
                    $"{context}: the data must contain coarse holes outside the fine global mask.");
    }

    private static Level CreateLevel(Random random, int width, int height)
    {
        var pixelCount = width * height;
        var pixels = new byte[pixelCount * 3];
        random.NextBytes(pixels);
        var targetPixels = new byte[pixelCount * 3];
        random.NextBytes(targetPixels);
        return new Level(width, height, pixels, RandomMask(random, pixelCount, 0.20f),
                         RandomMask(random, pixelCount, 0.15f), targetPixels,
                         RandomMask(random, pixelCount, 0.25f), RandomMask(random, pixelCount, 0.12f));
    }

    private static async Task<Direct3D12PatchWorkset> CreateWorksetAsync(Direct3D12Prototype prototype, Level level)
    {
        return await prototype.CreatePatchWorksetAsync(
            level.SourcePixels, level.SourceMask, level.SourceGlobal,
            level.TargetPixels, level.TargetMask, level.TargetGlobal, level.Width, level.Height);
    }

    // 工作集的完整期望快照：source 半边是上传内容，target 半边由调用方给出。
    private static PatchWorksetSnapshot SnapshotOf(Level level, Target target)
    {
        return SnapshotOf(level, target.Pixels, target.Invalid, target.Global);
    }

    private static PatchWorksetSnapshot SnapshotOf(Level level, byte[] targetPixels, byte[] targetInvalid,
                                                   byte[] targetGlobal)
    {
        return new PatchWorksetSnapshot(
            level.SourcePixels, level.SourceMask, MergeMasks(level.SourceMask, level.SourceGlobal),
            level.SourceGlobal, targetPixels, targetInvalid, targetGlobal);
    }

    private static byte[] MergeMasks(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = left[index] != 0 || right[index] != 0 ? (byte)1 : (byte)0;
        }

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
        // 逐字节相等才通过；相等时直接返回，不相等时交给 NUnit 输出具体差异。
        if (actual.AsSpan().SequenceEqual(expected))
        {
            return;
        }

        Assert.That(actual, Is.EqualTo(expected), $"{context}: {name}");
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
