using Banned.PatchMatch.Direct3D12;

namespace Banned.PatchMatch.Direct3D12.Test;

public sealed class DownsamplePrototypeTests
{
    private static readonly int[] DownsampleKernel = [1, 5, 10, 10, 5, 1];

    [TestCase(false, false, false, false)]
    [TestCase(false, false, false, true)]
    [TestCase(false, false, true, false)]
    [TestCase(false, false, true, true)]
    [TestCase(false, true, false, false)]
    [TestCase(false, true, false, true)]
    [TestCase(false, true, true, false)]
    [TestCase(false, true, true, true)]
    [TestCase(true, false, false, false)]
    [TestCase(true, false, false, true)]
    [TestCase(true, false, true, false)]
    [TestCase(true, false, true, true)]
    [TestCase(true, true, false, false)]
    [TestCase(true, true, false, true)]
    [TestCase(true, true, true, false)]
    [TestCase(true, true, true, true)]
    public async Task DownsampleMatchesIndependentReference(
        bool useWarp, bool oddWidth, bool oddHeight, bool hasGlobalMask)
    {
        var coarseWidth  = oddWidth ? 33 : 32;
        var coarseHeight = oddHeight ? 25 : 24;
        var coarsePixels = coarseWidth * coarseHeight;
        var random       = new Random(1000 + (useWarp ? 50 : 0) + (oddWidth ? 1 : 0) + (oddHeight ? 2 : 0)
                                      + (hasGlobalMask ? 4 : 0));
        var pixels       = new byte[coarsePixels * 3];
        random.NextBytes(pixels);
        // 纯随机蒙版下，6x6 窗口里的采样点几乎不可能全是孔洞（或全部被全局排除），结果会退化成
        // "全非孔洞、全未排除"，区分不了求 AND 与求 OR 之类的错误。所以除随机散布外再各放一个实心块，
        // 保证结果的孔洞与全局两种取值都出现。
        var mask = RandomMask(random, coarsePixels, 0.25f);
        FillRectangle(mask, coarseWidth, row : 2, column : 2, rowCount : 12, columnCount : 12);
        var global = RandomMask(random, coarsePixels, 0.15f);
        if (hasGlobalMask)
        {
            FillRectangle(global, coarseWidth, row : 16, column : 20, rowCount : 8, columnCount : 12);
        }

        var expected = DownsampleCpuReference(pixels, mask, hasGlobalMask ? global : null, coarseWidth, coarseHeight);
        Assert.That(expected.SourceMask, Has.Some.Not.EqualTo(0), "The reference must produce hole pixels.");
        Assert.That(expected.SourceMask, Has.Some.EqualTo(0), "The reference must produce non-hole pixels.");
        Assert.That(expected.SourceGlobal, Has.Some.EqualTo(0), "The reference must produce non-excluded pixels.");
        if (hasGlobalMask)
        {
            Assert.That(expected.SourceGlobal, Has.Some.Not.EqualTo(0), "The reference must produce globally masked pixels.");
            // 区分力：同一份数据按"跳过所有 invalid"（把仅全局排除的像素也排除出颜色平均）计算，必须与正确参考
            // 不同，否则本用例发现不了这种错误。
            var skipAllInvalid = DownsampleCpuReference(pixels, mask, global, coarseWidth, coarseHeight,
                                                        skipAllInvalid : true);
            Assert.That(CountDifferingPixels(expected, skipAllInvalid), Is.GreaterThan(0),
                        "The test data must distinguish skipping holes only from skipping every invalid pixel.");
        }
        else
        {
            Assert.That(expected.SourceGlobal, Has.All.EqualTo(0));
        }

        // 粗层的 target 半边与 source 半边不同，证明下采样只读 source。
        var targetPixels = new byte[coarsePixels * 3];
        random.NextBytes(targetPixels);
        var targetMask = RandomMask(random, coarsePixels, 0.3f);
        using var prototype = CreatePrototype(useWarp);
        using var coarse = await prototype.CreatePatchWorksetAsync(
            pixels, mask, hasGlobalMask ? global : ReadOnlyMemory<byte>.Empty,
            targetPixels, targetMask, ReadOnlyMemory<byte>.Empty, coarseWidth, coarseHeight);
        var coarseBefore = await prototype.ReadPatchWorksetAsync(coarse);
        using var downsampled = await prototype.CreateDownsampledPatchWorksetAsync(coarse);
        var actual      = await prototype.ReadPatchWorksetAsync(downsampled);
        var coarseAfter = await prototype.ReadPatchWorksetAsync(coarse);

        Assert.That(downsampled.Width, Is.EqualTo(coarseWidth / 2));
        Assert.That(downsampled.Height, Is.EqualTo(coarseHeight / 2));
        AssertSnapshotEquals(actual, expected, $"{coarseWidth}x{coarseHeight} downsampled");
        AssertSnapshotEquals(coarseAfter, coarseBefore, $"{coarseWidth}x{coarseHeight} coarse workset");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleAveragesGloballyExcludedNonHolePixels(bool useWarp)
    {
        // 左半边（列 0-5）被全局排除但不是孔洞，颜色 255；右半边颜色 0。整幅图没有孔洞。
        // CPU 语义是只跳过孔洞，全局排除的像素仍然参与颜色平均：窗口完全落在左半边的两列结果应为 255。
        // 如果错误地跳过所有 invalid，这两列的 kernelSum 为 0，会变成孔洞。
        const int width  = 12;
        const int height = 12;
        var pixels = new byte[width * height * 3];
        var global = new byte[width * height];
        var empty  = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value  = x < 6 ? (byte)255 : (byte)0;
                var offset = (y * width + x) * 3;
                pixels[offset]     = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                global[y * width + x] = x < 6 ? (byte)1 : (byte)0;
            }
        }

        // 手算期望（行方向可分离，每行相同）。采样列的核权重依次是 [1, 5, 10, 10, 5, 1]：
        // 列 0-1 的采样列全为 255 → 255；列 2 的采样列是 [255 x4 | 0 x2] → 255 * 26 / 32 = 207；
        // 列 3 是 [255 x2 | 0 x4] → 255 * 6 / 32 = 47；列 4-5 全为 0 → 0。
        byte[] expectedBrightness = [255, 255, 207, 47, 0, 0];
        using var prototype = CreatePrototype(useWarp);
        using var coarse = await prototype.CreatePatchWorksetAsync(
            pixels, empty, global, pixels, empty, global, width, height);
        using var downsampled = await prototype.CreateDownsampledPatchWorksetAsync(coarse);
        var actual = await prototype.ReadPatchWorksetAsync(downsampled);

        Assert.That(downsampled.Width, Is.EqualTo(6));
        Assert.That(downsampled.Height, Is.EqualTo(6));
        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 6; x++)
            {
                var pixel  = y * 6 + x;
                var offset = pixel * 3;
                var brightness = expectedBrightness[x];
                Assert.That((actual.SourcePixels[offset], actual.SourcePixels[offset + 1], actual.SourcePixels[offset + 2]),
                            Is.EqualTo((brightness, brightness, brightness)), $"pixel ({x}, {y}) color");
                Assert.That(actual.SourceMask[pixel], Is.EqualTo(0), $"pixel ({x}, {y}) hole");
                Assert.That(actual.SourceGlobal[pixel], Is.EqualTo(x < 2 ? 1 : 0), $"pixel ({x}, {y}) global");
                Assert.That(actual.SourceInvalid[pixel], Is.EqualTo(x < 2 ? 1 : 0), $"pixel ({x}, {y}) invalid");
                Assert.That(actual.TargetGlobal[pixel], Is.EqualTo(x < 2 ? 1 : 0), $"pixel ({x}, {y}) target global");
                // target 孔洞为空，合并后的 invalid 等于 global：被全局排除的最粗层 target 像素在距离里必须算无效。
                Assert.That(actual.TargetInvalid[pixel], Is.EqualTo(x < 2 ? 1 : 0), $"pixel ({x}, {y}) target invalid");
            }
        }

        var expected = DownsampleCpuReference(pixels, empty, global, width, height);
        AssertSnapshotEquals(actual, expected, "left half globally excluded");
        var skipAllInvalid = DownsampleCpuReference(pixels, empty, global, width, height, skipAllInvalid : true);
        Assert.That(skipAllInvalid.SourceMask, Has.Some.Not.EqualTo(0),
                    "Skipping every invalid pixel would turn the globally excluded columns into holes.");
        Assert.That(CountDifferingPixels(expected, skipAllInvalid), Is.GreaterThan(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleMatchesReferenceForEverySmallSize(bool useWarp)
    {
        // 2..10 的全部宽高组合：结果从 1x1 起，覆盖奇偶尺寸链、图像字与蒙版字的尾部不对齐，
        // 以及只有 4 字节大的缓冲。每 4 种尺寸轮换一组数据形态。
        using var prototype = CreatePrototype(useWarp);
        var random     = new Random(useWarp ? 7474 : 6363);
        var totalHoles = 0;
        var totalGloballyMasked = 0;
        var totalResultPixels   = 0;
        var totalDifferingPixels = 0;
        for (var height = 2; height <= 10; height++)
        {
            for (var width = 2; width <= 10; width++)
            {
                var pixelCount = width * height;
                var pixels = new byte[pixelCount * 3];
                random.NextBytes(pixels);
                var profile = (width + height) % 4;
                var hasGlobalMask   = profile != 2;
                var valuedMaskBytes = profile == 3;
                var holeDensity     = profile switch { 0 => 0.10f, 1 => 0.50f, 2 => 0.50f, _ => 0.20f };
                var globalDensity   = profile switch { 0 => 0.10f, 1 => 0.30f, 2 => 0.00f, _ => 0.30f };
                var mask = valuedMaskBytes ? RandomValuedMask(random, pixelCount, holeDensity)
                                           : RandomMask(random, pixelCount, holeDensity);
                var global = !hasGlobalMask ? new byte[pixelCount]
                            : valuedMaskBytes ? RandomValuedMask(random, pixelCount, globalDensity)
                                              : RandomMask(random, pixelCount, globalDensity);
                if (profile == 2)
                {
                    FillRectangle(mask, width, row : 0, column : 0, Math.Min(height, 6), Math.Min(width, 6));
                }

                if (profile == 3)
                {
                    FillRectangle(global, width, row : 0, column : 0, Math.Min(height, 6), Math.Min(width, 6), (byte)0xa5);
                }

                var expected = DownsampleCpuReference(pixels, mask, hasGlobalMask ? global : null, width, height);
                totalResultPixels += expected.SourceMask.Length;
                totalHoles += expected.SourceMask.Count(value => value != 0);
                totalGloballyMasked += expected.SourceGlobal.Count(value => value != 0);
                if (hasGlobalMask)
                {
                    var skipAllInvalid = DownsampleCpuReference(pixels, mask, global, width, height, skipAllInvalid : true);
                    totalDifferingPixels += CountDifferingPixels(expected, skipAllInvalid);
                }

                using var coarse = await prototype.CreatePatchWorksetAsync(
                    pixels, mask, hasGlobalMask ? global : ReadOnlyMemory<byte>.Empty,
                    pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
                using var downsampled = await prototype.CreateDownsampledPatchWorksetAsync(coarse);
                var actual = await prototype.ReadPatchWorksetAsync(downsampled);

                Assert.That(downsampled.Width, Is.EqualTo(width / 2), $"{width}x{height} result width");
                Assert.That(downsampled.Height, Is.EqualTo(height / 2), $"{width}x{height} result height");
                AssertSnapshotEquals(actual, expected, $"{width}x{height} downsampled");
            }
        }

        // 数据自检：整轮扫描的结果里必须两种取值都出现过，且"只跳过孔洞"与"跳过所有 invalid"必须分出差异。
        Assert.That(totalHoles, Is.GreaterThan(0));
        Assert.That(totalHoles, Is.LessThan(totalResultPixels));
        Assert.That(totalGloballyMasked, Is.GreaterThan(0));
        Assert.That(totalGloballyMasked, Is.LessThan(totalResultPixels));
        Assert.That(totalDifferingPixels, Is.GreaterThan(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleMatchesReferenceAtDispatchGroupBoundaries(bool useWarp)
    {
        // dispatch 派出的是 imageWordCount + maskWordCount 个线程，再向上取整到 64 个一组。小图取整出来的余量
        // 会掩盖"线程数算少了"的错误：例如 16x12 的结果是 144 个图像字 + 48 个蒙版字，而只按 144 派发时
        // 取整后正好是 192 个线程。下面前三个尺寸的图像字数正好是 64 的整数倍（256 像素 → 192 字），
        // 后两个尺寸所需的线程数超过只按图像字取整的组数（289 像素 → 290 > 4 组的 256，650 像素 → 651 > 8 组的 512）。
        // 每个尺寸都带孔洞块与全局块，蒙版字没被写出时结果必然对不上。
        using var prototype = CreatePrototype(useWarp);
        var random = new Random(useWarp ? 3535 : 2424);
        foreach (var (width, height) in new[] {(32, 32), (33, 33), (64, 16), (34, 34), (52, 50)})
        {
            var pixelCount = width * height;
            var pixels = new byte[pixelCount * 3];
            random.NextBytes(pixels);
            var mask   = RandomMask(random, pixelCount, 0.25f);
            var global = RandomMask(random, pixelCount, 0.15f);
            FillRectangle(mask, width, row : 0, column : 0, rowCount : 6, columnCount : 6);
            FillRectangle(global, width, row : 8, column : 8, rowCount : 8, columnCount : 8);
            var expected = DownsampleCpuReference(pixels, mask, global, width, height);
            Assert.That(expected.SourceMask, Has.Some.Not.EqualTo(0), $"{width}x{height} reference holes");
            Assert.That(expected.SourceGlobal, Has.Some.Not.EqualTo(0), $"{width}x{height} reference global");

            using var coarse = await prototype.CreatePatchWorksetAsync(
                pixels, mask, global, pixels, mask, global, width, height);
            using var downsampled = await prototype.CreateDownsampledPatchWorksetAsync(coarse);
            var actual = await prototype.ReadPatchWorksetAsync(downsampled);

            Assert.That(downsampled.Width, Is.EqualTo(width / 2), $"{width}x{height} result width");
            Assert.That(downsampled.Height, Is.EqualTo(height / 2), $"{width}x{height} result height");
            AssertSnapshotEquals(actual, expected, $"{width}x{height} downsampled");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleChainsDownToASinglePixel(bool useWarp)
    {
        // 连续下采样到 1x1：100x76 → 50x38 → 25x19 → 12x9 → 6x4 → 3x2 → 1x1。每一层的输出都是下一层的 SRV 输入，
        // 证明上一层写完后的缓冲确实可供读取，并且 shader 写出的孔洞蒙版能继续被下一层使用。
        const int width  = 100;
        const int height = 76;
        var pixelCount = width * height;
        var random     = new Random(useWarp ? 1919 : 1818);
        var pixels = new byte[pixelCount * 3];
        random.NextBytes(pixels);
        var mask   = RandomMask(random, pixelCount, 0.25f);
        var global = RandomMask(random, pixelCount, 0.15f);
        FillRectangle(mask, width, row : 20, column : 30, rowCount : 56, columnCount : 40);
        FillRectangle(global, width, row : 60, column : 0, rowCount : 16, columnCount : 30);
        using var prototype = CreatePrototype(useWarp);
        var worksets = new List<Direct3D12PatchWorkset>
        {
            await prototype.CreatePatchWorksetAsync(pixels, mask, global, pixels, mask, global, width, height)
        };
        try
        {
            var currentWidth  = width;
            var currentHeight = height;
            var current = new PatchWorksetSnapshot(pixels, mask, MergeMasks(mask, global), global, pixels, mask, global);
            while (currentWidth >= 2 && currentHeight >= 2)
            {
                worksets.Add(await prototype.CreateDownsampledPatchWorksetAsync(worksets[^1]));
                current = DownsampleCpuReference(current.SourcePixels, current.SourceMask, current.SourceGlobal,
                                                 currentWidth, currentHeight);
                currentWidth  /= 2;
                currentHeight /= 2;
                var level = worksets[^1];
                var actual = await prototype.ReadPatchWorksetAsync(level);
                Assert.That(level.Width, Is.EqualTo(currentWidth), "level width");
                Assert.That(level.Height, Is.EqualTo(currentHeight), "level height");
                AssertSnapshotEquals(actual, current, $"{currentWidth}x{currentHeight} level");
            }

            // 100 → 50 → 25 → 12 → 6 → 3 → 1：3 / 2 = 1，链停在 1x1（宽或高小于 2 就不能再下采样）。
            Assert.That((currentWidth, currentHeight), Is.EqualTo((1, 1)));
            Assert.That(worksets[^1].Width, Is.EqualTo(1));
            Assert.That(worksets[^1].Height, Is.EqualTo(1));
            Assert.That(worksets, Has.Count.EqualTo(7));
        }
        finally
        {
            foreach (var workset in worksets)
            {
                workset.Dispose();
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleDispatchesAcrossTheXDimensionLimit(bool useWarp)
    {
        // 4240x4000 → 2120x2000 = 4,240,000 个结果像素，即 3,180,000 个图像字 + 1,060,000 个蒙版字。
        // 线程数超过单行上限（65,535 组 x 64 = 4,194,240），dispatch 分两行派发：第一行写完全部图像字和前
        // 1,014,240 个蒙版字，第二行只写其余蒙版字（结果图第 1913 行末尾 720 个像素及其下 86 行），线程号靠
        // dispatchWidth 根常量跨行累加。其余用例都只有一行线程组，覆盖不到这个根常量。
        const int width  = 4240;
        const int height = 4000;
        var pixelCount = width * height;
        var random     = new Random(useWarp ? 5959 : 4949);
        var pixels = new byte[pixelCount * 3];
        random.NextBytes(pixels);
        var mask   = RandomMask(random, pixelCount, 0.25f);
        var global = RandomMask(random, pixelCount, 0.15f);
        // 在第二行线程负责的区域里各放一个孔洞块与全局块，使这一行的期望值不为全零；
        // 否则"第二行根本没写、新缓冲恰好是零"也能蒙混过关。
        FillRectangle(mask, width, row : 3830, column : 400, rowCount : 150, columnCount : 400);
        FillRectangle(global, width, row : 3860, column : 1600, rowCount : 110, columnCount : 400);
        using var prototype = CreatePrototype(useWarp);
        using var coarse = await prototype.CreatePatchWorksetAsync(
            pixels, mask, global, pixels, mask, global, width, height);
        using var downsampled = await prototype.CreateDownsampledPatchWorksetAsync(coarse);
        var actual = await prototype.ReadPatchWorksetAsync(downsampled);

        var expected = DownsampleCpuReference(pixels, mask, global, width, height);
        var secondRowStart = 1914 * 2120;
        Assert.That(expected.SourceMask.Skip(secondRowStart).Any(value => value != 0), Is.True,
                    "The second dispatch row must contain hole pixels.");
        Assert.That(expected.SourceGlobal.Skip(secondRowStart).Any(value => value != 0), Is.True,
                    "The second dispatch row must contain globally masked pixels.");
        Assert.That(downsampled.Width, Is.EqualTo(2120));
        Assert.That(downsampled.Height, Is.EqualTo(2000));
        AssertSnapshotEquals(actual, expected, "4240x4000 downsampled");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadPatchWorksetReturnsEveryUploadedBuffer(bool useWarp)
    {
        // 63 像素：图像与蒙版缓冲都带不对齐的尾巴。
        const int width  = 9;
        const int height = 7;
        var pixelCount = width * height;
        var random     = new Random(useWarp ? 8181 : 7272);
        var sourcePixels = new byte[pixelCount * 3];
        var targetPixels = new byte[pixelCount * 3];
        random.NextBytes(sourcePixels);
        random.NextBytes(targetPixels);
        var sourceMask   = RandomMask(random, pixelCount, 0.3f);
        var sourceGlobal = RandomMask(random, pixelCount, 0.3f);
        var targetMask   = RandomMask(random, pixelCount, 0.3f);
        var targetGlobal = RandomMask(random, pixelCount, 0.3f);
        Assert.That(sourceMask, Is.Not.EqualTo(sourceGlobal));
        Assert.That(sourceMask, Is.Not.EqualTo(targetMask));
        Assert.That(sourceGlobal, Is.Not.EqualTo(targetGlobal));
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            sourcePixels, sourceMask, sourceGlobal, targetPixels, targetMask, targetGlobal, width, height);

        var snapshot = await prototype.ReadPatchWorksetAsync(workset);

        Assert.That(snapshot.SourcePixels, Is.EqualTo(sourcePixels));
        Assert.That(snapshot.SourceMask, Is.EqualTo(sourceMask));
        Assert.That(snapshot.SourceInvalid, Is.EqualTo(MergeMasks(sourceMask, sourceGlobal)));
        Assert.That(snapshot.SourceGlobal, Is.EqualTo(sourceGlobal));
        Assert.That(snapshot.TargetPixels, Is.EqualTo(targetPixels));
        Assert.That(snapshot.TargetInvalid, Is.EqualTo(MergeMasks(targetMask, targetGlobal)));
        Assert.That(snapshot.TargetGlobal, Is.EqualTo(targetGlobal));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleRejectsWorksetsSmallerThanTwoByTwo(bool useWarp)
    {
        using var prototype = CreatePrototype(useWarp);
        foreach (var (width, height) in new[] {(1, 4), (4, 1), (1, 1)})
        {
            var pixelCount = width * height;
            using var workset = await prototype.CreatePatchWorksetAsync(
                new byte[pixelCount * 3], new byte[pixelCount], ReadOnlyMemory<byte>.Empty,
                new byte[pixelCount * 3], new byte[pixelCount], ReadOnlyMemory<byte>.Empty, width, height);
            Assert.That(async () => await prototype.CreateDownsampledPatchWorksetAsync(workset),
                        Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleRejectsForeignOwnerAndDisposedWorkset(bool useWarp)
    {
        const int width  = 8;
        const int height = 6;
        var pixels = new byte[width * height * 3];
        var mask   = new byte[width * height];
        using var owner    = CreatePrototype(useWarp);
        using var stranger = CreatePrototype(useWarp);
        using var foreignWorkset = await stranger.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty, pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        Assert.That(async () => await owner.CreateDownsampledPatchWorksetAsync(foreignWorkset),
                    Throws.TypeOf<ArgumentException>());

        var disposedWorkset = await owner.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty, pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        disposedWorkset.Dispose();
        Assert.That(async () => await owner.CreateDownsampledPatchWorksetAsync(disposedWorkset),
                    Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownsampleHonorsCancellationBeforeSubmission(bool useWarp)
    {
        const int width  = 8;
        const int height = 6;
        var pixels = new byte[width * height * 3];
        var mask   = new byte[width * height];
        using var prototype = CreatePrototype(useWarp);
        using var workset = await prototype.CreatePatchWorksetAsync(
            pixels, mask, ReadOnlyMemory<byte>.Empty, pixels, mask, ReadOnlyMemory<byte>.Empty, width, height);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await prototype.CreateDownsampledPatchWorksetAsync(workset, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>
    /// 独立 CPU 参考：按 MaskedImage.Downsample 的语义（中心 (2cx, 2cy)，窗口 deltaY、deltaX 取 [-2, 3]，
    /// 核 [1, 5, 10, 10, 5, 1] 的外积，越界采样点跳过，只跳过孔洞，全局标记对界内采样点求 AND）计算一层下采样，
    /// 并按"下采样后的 source 加空孔洞蒙版"给出 target 半边：孔洞为空，所以 target invalid 等于 target global。与 GPU shader 相互独立。
    /// </summary>
    private static PatchWorksetSnapshot DownsampleCpuReference(
        byte[] pixels, byte[] mask, byte[]? globalMask, int width, int height, bool skipAllInvalid = false)
    {
        var newWidth     = width / 2;
        var newHeight    = height / 2;
        var pixelCount   = newWidth * newHeight;
        var resultPixels = new byte[pixelCount * 3];
        var resultMask   = new byte[pixelCount];
        var resultGlobal = new byte[pixelCount];
        for (var centerY = 0; centerY < newHeight; centerY++)
        {
            for (var centerX = 0; centerX < newWidth; centerX++)
            {
                bool globallyMasked = true;
                int channel0 = 0;
                int channel1 = 0;
                int channel2 = 0;
                int kernelSum = 0;
                for (var deltaY = -2; deltaY <= 3; deltaY++)
                {
                    var sourceY = centerY * 2 + deltaY;
                    if (sourceY < 0 || sourceY >= height)
                    {
                        continue;
                    }

                    for (var deltaX = -2; deltaX <= 3; deltaX++)
                    {
                        var sourceX = centerX * 2 + deltaX;
                        if (sourceX < 0 || sourceX >= width)
                        {
                            continue;
                        }

                        var source           = sourceY * width + sourceX;
                        var globallyExcluded = globalMask is not null && globalMask[source] != 0;
                        globallyMasked &= globallyExcluded;
                        if (mask[source] != 0 || (skipAllInvalid && globallyExcluded))
                        {
                            continue;
                        }

                        var kernel = DownsampleKernel[deltaY + 2] * DownsampleKernel[deltaX + 2];
                        var offset = source * 3;
                        channel0 += pixels[offset] * kernel;
                        channel1 += pixels[offset + 1] * kernel;
                        channel2 += pixels[offset + 2] * kernel;
                        kernelSum += kernel;
                    }
                }

                var target = centerY * newWidth + centerX;
                resultGlobal[target] = globallyMasked ? (byte)1 : (byte)0;
                if (kernelSum == 0)
                {
                    resultMask[target] = 1;
                    continue;
                }

                var targetOffset = target * 3;
                resultPixels[targetOffset]     = (byte)(channel0 / kernelSum);
                resultPixels[targetOffset + 1] = (byte)(channel1 / kernelSum);
                resultPixels[targetOffset + 2] = (byte)(channel2 / kernelSum);
            }
        }

        // 新 target 的孔洞蒙版为空，工作集只保存合并后的 invalid（孔洞 | 全局），所以它等于 target global。
        return new PatchWorksetSnapshot(
            resultPixels, resultMask, MergeMasks(resultMask, resultGlobal), resultGlobal,
            resultPixels, resultGlobal, resultGlobal);
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

    private static int CountDifferingPixels(PatchWorksetSnapshot left, PatchWorksetSnapshot right)
    {
        var count = 0;
        for (var pixel = 0; pixel < left.SourceMask.Length; pixel++)
        {
            var offset = pixel * 3;
            if (left.SourcePixels[offset] != right.SourcePixels[offset] ||
                left.SourcePixels[offset + 1] != right.SourcePixels[offset + 1] ||
                left.SourcePixels[offset + 2] != right.SourcePixels[offset + 2] ||
                left.SourceMask[pixel] != right.SourceMask[pixel] ||
                left.SourceGlobal[pixel] != right.SourceGlobal[pixel])
            {
                count++;
            }
        }

        return count;
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

    private static byte[] RandomMask(Random random, int count, float density)
    {
        var result = new byte[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = random.NextSingle() < density ? (byte)1 : (byte)0;
        }

        return result;
    }

    // 非 0/1 的蒙版字节：CPU 语义只判断"是否为 0"。
    private static byte[] RandomValuedMask(Random random, int count, float density)
    {
        var result = new byte[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = random.NextSingle() < density ? (byte)random.Next(1, 256) : (byte)0;
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
