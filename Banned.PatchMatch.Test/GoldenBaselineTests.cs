using System.Security.Cryptography;

namespace Banned.PatchMatch.Test;

/// <summary>
/// 阶段 0 冻结的标量 CPU 基线：固定种子合成语料 + SHA256 黄金哈希。
/// SIMD 或任何内部改动如果改变了这些输出，必须先确认是有意的行为变更并更新基线。<br/>
/// Stage-0 frozen scalar CPU baseline: seeded synthetic corpus with SHA-256 golden hashes.
/// Any internal change (including the SIMD prototype) that alters these outputs is a
/// behavior change and must be consciously accepted with updated baselines.
/// </summary>
public class GoldenBaselineTests
{
    [TestCaseSource(nameof(GoldenScenarios))]
    public void Inpaint_MatchesFrozenGoldenHash(GoldenScenario scenario)
    {
        var result = scenario.Run();
        var hash   = Convert.ToHexString(SHA256.HashData(result));
        Assert.That(hash, Is.EqualTo(scenario.ExpectedHash), $"golden output changed for scenario '{scenario.Name}'");
    }

    public static IEnumerable<GoldenScenario> GoldenScenarios()
    {
        yield return new GoldenScenario(
                                        "textured-center-rect-r2",
                                        96, 72, CreateTextured(96, 72, seed : 7),
                                        CreateCenterRect(96, 72, 24, 20, 72, 52), null,
                                        new PatchMatchOptions { PatchRadius = 2, RandomSeed = 1212 },
                                        "4BE77736671B7C1619776753E331D8AD19D7C65BE6E06165E909170AB729D0FD");

        yield return new GoldenScenario(
                                        "gradient-scattered-r3",
                                        96, 72, CreateGradient(96, 72), CreateScattered(96, 72, seed : 42), null,
                                        new PatchMatchOptions { PatchRadius = 3, RandomSeed = 99001 },
                                        "5F49DACA2B7C206FC4BD40B842F0DBA10FC37A32C8E7F72B4864F086C233D969");

        yield return new GoldenScenario(
                                        "lines-center-rect-global-r2",
                                        64, 64, CreateLines(64, 64), CreateCenterRect(64, 64, 16, 16, 48, 48),
                                        CreateScattered(64, 64, seed : 5),
                                        new PatchMatchOptions { PatchRadius = 2, RandomSeed = 1212 },
                                        "4A3C9B488DAACA34C954C717C1FCB7144C32ED13B625CAA80D083986C0FA19D0");

        yield return new GoldenScenario(
                                        "white-pixel-hole-r2",
                                        64, 64, CreateWhiteHoleGradient(64, 64), null, null,
                                        new PatchMatchOptions { PatchRadius = 2, RandomSeed = 1212 },
                                        "01C5185A2BBDAD5B1A70BC53591451A8C71ADA6E246136D3FE1590BE1D6307DA");

        yield return new GoldenScenario(
                                        "regularity-guide-r2",
                                        64, 64, CreateLines(64, 64), CreateCenterRect(64, 64, 20, 20, 44, 44), null,
                                        new PatchMatchOptions { PatchRadius = 2, RandomSeed = 1212 },
                                        "AEF4289C26301A91A716AC10372991594D7E310569D9DE7C076A6C79E73E4D0F",
                                        CreateGuideMap(64, 64));
    }

    public sealed record GoldenScenario(
        string            Name,
        int               Width,
        int               Height,
        byte[]            Image,
        byte[]?           Mask,
        byte[]?           GlobalMask,
        PatchMatchOptions Options,
        string            ExpectedHash,
        float[]?          GuideMap = null)
    {
        public byte[] Run()
        {
            if (GuideMap is not null)
            {
                return PatchMatchInpainter.InpaintRegularity(Image, Mask!, GuideMap, Width, Height,
                                                             options : Options);
            }

            if (Mask is null)
            {
                return PatchMatchInpainter.Inpaint(Image, Width, Height, Options);
            }

            return GlobalMask is null
                ? PatchMatchInpainter.Inpaint(Image, Mask, Width, Height, Options)
                : PatchMatchInpainter.Inpaint(Image, Mask, GlobalMask, Width, Height, Options);
        }
    }

    private static byte[] CreateGradient(int width, int height)
    {
        var result = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x)      * 3;
                result[offset]     = (byte)(255 * x / width);
                result[offset + 1] = (byte)(255 * y / height);
                result[offset + 2] = (byte)(128 + (127 * (x + y) / (width + height)));
            }
        }

        return result;
    }

    private static byte[] CreateTextured(int width, int height, int seed)
    {
        var result = new byte[width * height * 3];
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width)           + x) * 3;
                var grain  = (byte)(random.Next(64) + ((x / 8 + y / 8) % 2) * 60);
                result[offset]     = grain;
                result[offset + 1] = (byte)(grain / 2);
                result[offset + 2] = (byte)(255 - grain);
            }
        }

        return result;
    }

    private static byte[] CreateLines(int width, int height)
    {
        var result = new byte[width * height * 3];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            result[pixel * 3]     = 250;
            result[pixel * 3 + 1] = 250;
            result[pixel * 3 + 2] = 245;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x % 37 is 0 or 1 || y % 53 is 0 or 1)
                {
                    var offset = ((y * width) + x) * 3;
                    result[offset]     = 20;
                    result[offset + 1] = 20;
                    result[offset + 2] = 30;
                }
            }
        }

        return result;
    }

    private static byte[] CreateWhiteHoleGradient(int width, int height)
    {
        var result = CreateGradient(width, height);
        for (var y = height / 3; y < 2 * height / 3; y++)
        {
            for (var x = width / 3; x < 2 * width / 3; x++)
            {
                var offset = ((y * width) + x) * 3;
                result[offset]     = byte.MaxValue;
                result[offset + 1] = byte.MaxValue;
                result[offset + 2] = byte.MaxValue;
            }
        }

        return result;
    }

    private static byte[] CreateCenterRect(int width, int height, int x0, int y0, int x1, int y1)
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

    private static byte[] CreateScattered(int width, int height, int seed)
    {
        var result = new byte[width * height];
        var random = new Random(seed);
        for (var blob = 0; blob < 8; blob++)
        {
            var centerX = random.Next(width);
            var centerY = random.Next(height);
            var radius  = random.Next(4, 12);
            for (var y = centerY - radius; y <= centerY + radius; y++)
            {
                for (var x = centerX - radius; x <= centerX + radius; x++)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height)
                    {
                        continue;
                    }

                    var dx = x - centerX;
                    var dy = y - centerY;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        result[(y * width) + x] = 1;
                    }
                }
            }
        }

        return result;
    }

    private static float[] CreateGuideMap(int width, int height)
    {
        var result = new float[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                result[offset]     = (float)x / width;
                result[offset + 1] = (float)y / height;
                result[offset + 2] = 0;
            }
        }

        return result;
    }
}
