using Banned.PatchMatch.Internal;

namespace Banned.PatchMatch.Test;

/// <summary>
/// 阶段 1 SIMD 原型差分测试：所有向量路径（两种布局 × 三种向量宽度）与冻结的标量参考
/// 在随机语料、图像边界、不同半径、孔洞蒙版与全局蒙版上逐字节一致。<br/>
/// Phase-1 SIMD differential tests: every vector path (both layouts × three vector widths)
/// must match the frozen scalar reference exactly across random corpora, image borders,
/// varying patch radii, hole masks and global masks.
/// </summary>
[TestFixture]
public class SimdKernelDifferentialTests
{
    private static readonly (int Width, int Height)[] Sizes         = [(33, 17), (64, 64), (65, 49), (97, 53)];
    private static readonly int[]                     Radii         = [0, 2, 3, 8];
    private static readonly float[]                   MaskDensities = [0f, 0.35f, 0.9f];

    [Test]
    public void VectorPathsMatchScalarReference()
    {
        var checks = 0;
        try
        {
            foreach (var globallyMasked in new[] { false, true })
            foreach (var (width, height) in Sizes)
            foreach (var radius in Radii)
            foreach (var density in MaskDensities)
            {
                var seed = checked((width * 1_000_003)         + (height * 1009) + (radius * 101) +
                                   ((int)(density * 100) << 2) + (globallyMasked ? 7 : 0));
                var random = new Random(seed);
                var source = MaskedImage.Create(RandomBytes(random, width * height * 3),
                                                RandomMask(random, width  * height, density),
                                                globallyMasked
                                                    ? RandomMask(random, width * height, 0.2f)
                                                    : Array.Empty<byte>(),
                                                width, height);
                var target = MaskedImage.Create(RandomBytes(random, width * height * 3),
                                                RandomMask(random, width  * height, density),
                                                globallyMasked
                                                    ? RandomMask(random, width * height, 0.2f)
                                                    : Array.Empty<byte>(),
                                                width, height);

                for (var sample = 0; sample < 80; sample++)
                {
                    // 采样包含越界中心，覆盖传播步骤可能传入的边缘坐标。
                    var sourceY = random.Next(-2, height + 2);
                    var sourceX = random.Next(-2, width  + 2);
                    var targetY = random.Next(-2, height + 2);
                    var targetX = random.Next(-2, width  + 2);
                    var expected = PatchSsdDistanceMetric.CalculateImageDistanceScalar(source, sourceY, sourceX,
                             target, targetY, targetX,
                             radius);
                    foreach (var vectorBytes in new int?[] { 16, 32, 64 })
                    foreach (var featureLayout in new[] { false, true })
                    {
                        PatchSsdDistanceMetric.ForceVectorBytes = vectorBytes;
                        PatchSsdDistanceMetric.UseFeatureLayout = featureLayout;
                        var actual = PatchSsdDistanceMetric.CalculateImageDistance(source, sourceY, sourceX,
                                 target, targetY, targetX, radius);
                        checks++;
                        Assert.That(actual, Is.EqualTo(expected),
                                    $"w={width} h={height} r={radius} density={density} global={globallyMasked} " +
                                    $"vectorBytes={vectorBytes} features={featureLayout} "                        +
                                    $"at ({sourceY},{sourceX})->({targetY},{targetX})");
                    }
                }
            }
        }
        finally
        {
            PatchSsdDistanceMetric.ForceVectorBytes = null;
            PatchSsdDistanceMetric.UseFeatureLayout = true;
        }

        Console.WriteLine($"differential checks passed: {checks}");
    }

    [Test]
    public void VectorPathsMatchScalarReferenceOnSelfDistance()
    {
        // 对应基准微内核的调用形态：source 与 target 为同一实例。
        var random = new Random(2026);
        var width  = 96;
        var height = 72;
        var image = MaskedImage.Create(RandomBytes(random, width * height * 3),
                                       RandomMask(random, width  * height, 0.3f),
                                       RandomMask(random, width  * height, 0.1f),
                                       width, height);
        try
        {
            for (var sample = 0; sample < 300; sample++)
            {
                var y        = random.Next(height);
                var x        = random.Next(width);
                var oy       = random.Next(height);
                var ox       = random.Next(width);
                var expected = PatchSsdDistanceMetric.CalculateImageDistanceScalar(image, y, x, image, oy, ox, 3);
                foreach (var vectorBytes in new int?[] { 16, 32, 64 })
                foreach (var featureLayout in new[] { false, true })
                {
                    PatchSsdDistanceMetric.ForceVectorBytes = vectorBytes;
                    PatchSsdDistanceMetric.UseFeatureLayout = featureLayout;
                    Assert.That(PatchSsdDistanceMetric.CalculateImageDistance(image, y, x, image, oy, ox, 3),
                                Is.EqualTo(expected),
                                $"self distance at ({y},{x})->({oy},{ox}) vectorBytes={vectorBytes} features={featureLayout}");
                }
            }
        }
        finally
        {
            PatchSsdDistanceMetric.ForceVectorBytes = null;
            PatchSsdDistanceMetric.UseFeatureLayout = true;
        }
    }

    [Test]
    public void InvalidBitsTrackMaskMutationsAcrossWordBoundaries()
    {
        const int width      = 70;
        const int height     = 2;
        var       mask       = new byte[width * height];
        var       globalMask = new byte[mask.Length];
        mask[1]        = 1;
        globalMask[65] = 1;
        var image = MaskedImage.Create(new byte[mask.Length * 3], mask, globalMask, width, height);

        Assert.Multiple((Action)(() =>
        {
            Assert.That(IsBitSet(image.InvalidBits, 1), Is.True);
            Assert.That(IsBitSet(image.InvalidBits, 65), Is.True);
            Assert.That(IsBitSet(image.InvalidBits, 64), Is.False);
        }));

        image.SetMask(0, 1, false);
        image.SetMask(1, 3, true); // index 73: rebuild must cross the second ulong word.
        Assert.Multiple((Action)(() =>
        {
            Assert.That(IsBitSet(image.InvalidBits, 1), Is.False);
            Assert.That(IsBitSet(image.InvalidBits, 65), Is.True);
            Assert.That(IsBitSet(image.InvalidBits, 73), Is.True);
        }));

        image.ClearMask();
        Assert.Multiple((Action)(() =>
        {
            Assert.That(IsBitSet(image.InvalidBits, 65), Is.True, "global mask must survive ClearMask");
            Assert.That(IsBitSet(image.InvalidBits, 73), Is.False);
        }));
    }

    [Test]
    public void SsdSinksMatchScalarForAllLengths()
    {
        // 逐长度锤尾部逻辑：0 到远超最宽向量的全部长度。
        var random = new Random(42);
        var a      = RandomBytes(random, 300);
        var b      = RandomBytes(random, 300);
        for (var length = 0; length <= 260; length++)
        {
            long expected = 0;
            for (var i = 0; i < length; i++)
            {
                var difference = a[i] - b[i];
                expected += (long)difference * difference;
            }

            var spanA = a.AsSpan(0, length);
            var spanB = b.AsSpan(0, length);

            var scalar = new ScalarSsdSink();
            scalar.Add(spanA, spanB);
            Assert.That(scalar.Total(), Is.EqualTo(expected), $"scalar len={length}");

            var vector128 = new Vector128SsdSink();
            vector128.Add(spanA, spanB);
            Assert.That(vector128.Total(), Is.EqualTo(expected), $"vector128 len={length}");

            var vector256 = new Vector256SsdSink();
            vector256.Add(spanA, spanB);
            Assert.That(vector256.Total(), Is.EqualTo(expected), $"vector256 len={length}");

            var vector512 = new Vector512SsdSink();
            vector512.Add(spanA, spanB);
            Assert.That(vector512.Total(), Is.EqualTo(expected), $"vector512 len={length}");
        }
    }

    [TestCase(16)]
    [TestCase(32)]
    [TestCase(64)]
    public void SsdSinkReductionDoesNotOverflow(int vectorBytes)
    {
        // Each uint accumulator lane stays below its limit, but merging four lanes
        // in uint overflows. The extra bytes also exercise all narrower tails.
        const int repetitions = 20_000;
        var a = new byte[2 * vectorBytes - 1];
        var b = new byte[a.Length];
        Array.Fill(b, byte.MaxValue);
        ISsdSink sink = vectorBytes switch
        {
            16 => new Vector128SsdSink(),
            32 => new Vector256SsdSink(),
            _ => new Vector512SsdSink(),
        };

        for (var i = 0; i < repetitions; i++)
        {
            sink.Add(a, b);
        }

        var expected = (long)repetitions * a.Length * 255 * 255;
        Assert.That(sink.Total(), Is.EqualTo(expected));
    }

    [TestCase(96)]
    [TestCase(127)]
    [TestCase(128)]
    public void LargePatchDistancesMatchScalarReference(int radius)
    {
        var size = 2 * radius + 5;
        var a = new byte[size * size * 3];
        var b = new byte[a.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // Complementary 2x2 blocks maximize both pixel and gradient differences.
            var value = (byte)(255 * (((x >> 1) ^ (y >> 1)) & 1));
            for (var channel = 0; channel < 3; channel++)
            {
                var offset = (y * size + x) * 3 + channel;
                a[offset] = value;
                b[offset] = (byte)(255 - value);
            }
        }

        var mask = new byte[size * size];
        var source = MaskedImage.Create(a, mask, Array.Empty<byte>(), size, size);
        var target = MaskedImage.Create(b, mask, Array.Empty<byte>(), size, size);
        var center = size / 2;
        var expected = PatchSsdDistanceMetric.CalculateImageDistanceScalar(
            source, center, center, target, center, center, radius);
        var previousWidth = PatchSsdDistanceMetric.ForceVectorBytes;
        var previousLayout = PatchSsdDistanceMetric.UseFeatureLayout;
        var previousScalar = PatchSsdDistanceMetric.ForceScalar;
        try
        {
            PatchSsdDistanceMetric.ForceScalar = false;
            foreach (var width in new[] { 16, 32, 64 })
            foreach (var layout in new[] { false, true })
            {
                PatchSsdDistanceMetric.ForceVectorBytes = width;
                PatchSsdDistanceMetric.UseFeatureLayout = layout;
                Assert.That(PatchSsdDistanceMetric.CalculateImageDistance(
                                source, center, center, target, center, center, radius),
                            Is.EqualTo(expected), $"radius={radius} width={width} features={layout}");
            }
        }
        finally
        {
            PatchSsdDistanceMetric.ForceVectorBytes = previousWidth;
            PatchSsdDistanceMetric.UseFeatureLayout = previousLayout;
            PatchSsdDistanceMetric.ForceScalar = previousScalar;
        }
    }

    [Test]
    public void InpaintOutputMatchesForcedScalar()
    {
        // 端到端字节一致性：默认（向量）路径与强制标量路径的完整修复输出必须相同，
        // 覆盖普通 mask、global mask 与不同半径。
        var       random     = new Random(7);
        const int width      = 96;
        const int height     = 72;
        var       image      = RandomBytes(random, width * height * 3);
        var       mask       = CenterRectangleMask(width, height, 24, 18, 72, 54);
        var       globalMask = RandomMask(new Random(11), width * height, 0.15f);

        try
        {
            foreach (var radius in new[] { 2, 3, 5 })
            {
                var options      = new PatchMatchOptions { PatchRadius = radius, RandomSeed = 1212 };
                var vectorResult = PatchMatchInpainter.Inpaint(image, mask, width, height, options);
                PatchSsdDistanceMetric.ForceScalar = true;
                var scalarResult = PatchMatchInpainter.Inpaint(image, mask, width, height, options);
                PatchSsdDistanceMetric.ForceScalar = false;
                Assert.That(vectorResult, Is.EqualTo(scalarResult).AsCollection, $"radius={radius}");

                var vectorGlobal = PatchMatchInpainter.Inpaint(image, mask, globalMask, width, height, options);
                PatchSsdDistanceMetric.ForceScalar = true;
                var scalarGlobal = PatchMatchInpainter.Inpaint(image, mask, globalMask, width, height, options);
                PatchSsdDistanceMetric.ForceScalar = false;
                Assert.That(vectorGlobal, Is.EqualTo(scalarGlobal).AsCollection, $"radius={radius} global");
            }
        }
        finally
        {
            PatchSsdDistanceMetric.ForceScalar = false;
        }
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var result = new byte[count];
        random.NextBytes(result);
        return result;
    }

    private static bool IsBitSet(ulong[] words, int bit)
    {
        return (words[bit >> 6] & (1ul << (bit & 63))) != 0;
    }

    private static byte[] RandomMask(Random random, int count, float density)
    {
        var result = new byte[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = random.NextSingle() < density ? (byte)1 : (byte)0;
        }

        return result;
    }

    private static byte[] CenterRectangleMask(int width, int height, int x0, int y0, int x1, int y1)
    {
        var result = new byte[width * height];
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                result[(y * width) + x] = 1;
            }
        }

        return result;
    }
}
