using Banned.PatchMatch;
using Banned.PatchMatch.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

// 阶段 0 基线工具：阶段耗时分析、合成语料生成、端到端基准、补丁距离微基准与真实语料基线固化。
// Stage-0 baseline tooling: stage profiling, synthetic corpus generation, end-to-end benchmarks,
// the patch-distance micro-benchmark and real-corpus baseline freezing. Run with -c Release.
switch (args.FirstOrDefault())
{
    case "stages" :
        RunStageProfile();
        break;
    case "corpus" :
        GenerateCorpus(args.Length > 1 ? args[1] : Path.Combine("artifacts", "corpus"));
        break;
    case "bench" :
        RunBenchmarks();
        break;
    case "realcorpus" :
        RunRealCorpus(args.Length > 1 ? args[1] : Path.Combine("artifacts", "tests"));
        break;
    default :
        Console.WriteLine("usage: Banned.PatchMatch.Bench <stages|corpus [dir]|bench|realcorpus [dir]>");
        Console.WriteLine("       build with -c Release for meaningful numbers");
        return 1;
}

return 0;

static void RunStageProfile()
{
    // 512x512 中心矩形 mask，典型对话框场景，跑 3 次取累计阶段占比。
    const int width  = 512;
    const int height = 512;
    var       image  = SyntheticImages.Textured(width, height, seed : 7);
    var       mask   = Masks.CenterRectangle(width, height, 128, 128, 384, 384);

    StageProfiler.Enabled = true;
    StageProfiler.Reset();

    const int runs  = 3;
    var       total = Stopwatch.StartNew();
    for (var run = 0; run < runs; run++)
    {
        PatchMatchInpainter.Inpaint(image, (byte[])mask.Clone(), width, height,
                                    new PatchMatchOptions { PatchRadius = 3, RandomSeed = 1212 });
    }

    total.Stop();

    Console.WriteLine($"stage profile: {width}x{height} center-rect mask, patch radius 3, {runs} runs");
    Console.WriteLine($"total {total.Elapsed.TotalMilliseconds / runs:F1} ms/run (wall)");
    Console.WriteLine("stage             ms/run   share");
    foreach (var (stage, ticks) in StageProfiler.ElapsedTicks.OrderBy(pair => -pair.Value))
    {
        var ms = ticks * 1000.0 / Stopwatch.Frequency / runs;
        Console.WriteLine($"{stage,-16} {ms,8:F1}  {100 * ms / (total.Elapsed.TotalMilliseconds / runs),5:F1}%");
    }

    StageProfiler.Enabled = false;
    BenchmarkDistanceShareInPipeline();
}

static void BenchmarkDistanceShareInPipeline()
{
    // 交替运行无探针与计时探针版本。两者的墙钟差值用于估算装饰器的全部开销，
    // 避免只修正距离计时的分子而不修正端到端分母。该结果仍是性能归因估计，
    // 阶段 1 的最终验收必须使用同会话的标量/SIMD 端到端对照。
    const int width  = 512;
    const int height = 512;
    var       image  = SyntheticImages.Textured(width, height, seed : 7);
    var       mask   = Masks.CenterRectangle(width, height, 128, 128, 384, 384);

    StageProfiler.Enabled = false;
    RunPipelineProbe(image, mask, width, height, new PatchSsdDistanceMetric(3));
    RunPipelineProbe(image, mask, width, height, new TimedDistanceMetric(new PatchSsdDistanceMetric(3)));

    const int samples        = 5;
    var       baselineWallMs = new double[samples];
    var       probedWallMs   = new double[samples];
    var       metricMs       = new double[samples];
    var       calls          = new long[samples];
    var       outputsStable  = true;
    byte[]?   reference      = null;
    for (var sample = 0; sample < samples; sample++)
    {
        PipelineProbeResult baseline;
        PipelineProbeResult probed;
        if (sample % 2 == 0)
        {
            baseline = RunPipelineProbe(image, mask, width, height, new PatchSsdDistanceMetric(3));
            probed = RunPipelineProbe(image, mask, width, height,
                                      new TimedDistanceMetric(new PatchSsdDistanceMetric(3)));
        }
        else
        {
            probed = RunPipelineProbe(image, mask, width, height,
                                      new TimedDistanceMetric(new PatchSsdDistanceMetric(3)));
            baseline = RunPipelineProbe(image, mask, width, height, new PatchSsdDistanceMetric(3));
        }

        reference ??= baseline.Output;
        outputsStable &= reference.AsSpan().SequenceEqual(baseline.Output) &&
                         reference.AsSpan().SequenceEqual(probed.Output);
        baselineWallMs[sample] = baseline.WallMilliseconds;
        probedWallMs[sample]   = probed.WallMilliseconds;
        metricMs[sample]       = probed.MetricMilliseconds;
        calls[sample]          = probed.Calls;
    }

    var baselineMedian = Median(baselineWallMs);
    var probedMedian   = Median(probedWallMs);
    var metricMedian   = Median(metricMs);
    var probeOverhead  = Math.Max(0, probedMedian - baselineMedian);
    var adjustedMetric = Math.Max(0, metricMedian - probeOverhead);
    var adjustedShare  = 100 * adjustedMetric / baselineMedian;
    var rawShare       = 100 * metricMedian   / probedMedian;
    Console.WriteLine();
    Console.WriteLine($"distance share probe ({samples} alternating baseline/instrumented pairs):");
    Console.WriteLine($"  baseline wall median {baselineMedian:F0} ms | instrumented wall median {probedMedian:F0} ms " +
                      $"| observed probe overhead {probeOverhead:F0} ms");
    Console.WriteLine($"  instrumented metric median {metricMedian:F0} ms ({rawShare:F0}% of instrumented wall)");
    Console.WriteLine($"  overhead-adjusted estimate {adjustedMetric:F0} ms ({adjustedShare:F0}% of baseline wall) " +
                      $"| calls {MedianLong(calls):F0}/run");
    Console.WriteLine($"  deterministic outputs: {outputsStable}; stable call count: {calls.All(call => call == calls[0])}");
}

static PipelineProbeResult RunPipelineProbe(byte[]               image, byte[] mask, int width, int height,
                                            IPatchDistanceMetric metric)
{
    var sw      = Stopwatch.StartNew();
    var initial = MaskedImage.Create(image, mask, default, width, height);
    var engine  = new InpaintingEngine(initial, metric, 1212, CancellationToken.None);
    var output  = engine.Run();
    sw.Stop();

    return metric is TimedDistanceMetric timed
        ? new PipelineProbeResult(sw.Elapsed.TotalMilliseconds,
                                  timed.Ticks * 1000.0 / Stopwatch.Frequency, timed.Calls, output)
        : new PipelineProbeResult(sw.Elapsed.TotalMilliseconds, 0, 0, output);
}

static double Median(double[] values)
{
    var ordered = (double[])values.Clone();
    Array.Sort(ordered);
    return ordered[ordered.Length / 2];
}

static double MedianLong(long[] values)
{
    var ordered = (long[])values.Clone();
    Array.Sort(ordered);
    return ordered[ordered.Length / 2];
}

static void RunBenchmarks()
{
    // 端到端热路径：合成纹理图 + 中心矩形 mask（约 25% 覆盖），patch radius 3。
    foreach (var size in new[] { 256, 512, 1024 })
    {
        var image    = SyntheticImages.Textured(size, size, seed : 7);
        var mask     = Masks.CenterRectangle(size, size, size / 4, size / 4, 3 * size / 4, 3 * size / 4);
        var options  = new PatchMatchOptions { PatchRadius = 3, RandomSeed = 1212 };
        var coldMask = (byte[])mask.Clone();
        var cold     = Stopwatch.StartNew();
        PatchMatchInpainter.Inpaint(image, coldMask, size, size, options);
        cold.Stop();

        var iterations = size >= 1024 ? 3 : 5;
        var samples    = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var sw = Stopwatch.StartNew();
            PatchMatchInpainter.Inpaint(image, (byte[])mask.Clone(), size, size, options);
            sw.Stop();
            samples[iteration] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine(
                          $"inpaint {size}x{size} r=3: cold {cold.Elapsed.TotalMilliseconds,8:F0} ms | " +
                          $"hot median {samples[samples.Length / 2],8:F0} ms | min {samples[0],8:F0} ms ({iterations} runs)");
    }

    BenchmarkDistanceKernel();
}

static void BenchmarkDistanceKernel()
{
    // 补丁距离内核微基准：同尺寸图像上随机像素对的距离计算吞吐。
    // 充分预热（10 个样本）后再采集 31 个独立样本。P10-P90 spread 用于描述
    // 稳态离散度；min/max 仅作为调度离群点披露。
    foreach (var size in new[] { 256, 512 })
    {
        const int patchRadius    = 3;
        var       image          = SyntheticImages.Textured(size, size, seed : 7);
        var       mask           = new byte[size * size];
        var       target         = MaskedImage.Create(image, mask, mask, size, size);
        var       random         = new Random(1212);
        const int callsPerSample = 4096;
        var       pairs          = new (int Y, int X)[callsPerSample];
        for (var i = 0; i < pairs.Length; i++)
        {
            pairs[i] = (random.Next(1, size - 1), random.Next(1, size - 1));
        }

        // 预热：让 JIT 分层编译与梯度缓存稳定。
        for (var warmup = 0; warmup < 10; warmup++)
        {
            RunDistanceSample(target, pairs, patchRadius);
        }

        const int samples   = 31;
        var       timings   = new double[samples];
        var       checksums = new long[samples];
        for (var sample = 0; sample < samples; sample++)
        {
            var sw = Stopwatch.StartNew();
            checksums[sample] = RunDistanceSample(target, pairs, patchRadius);
            sw.Stop();
            timings[sample] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        var median                = timings[samples           / 2];
        var percentile10          = timings[(samples - 1)     / 10];
        var percentile90          = timings[(samples - 1) * 9 / 10];
        var interPercentileSpread = 100  * (percentile90 - percentile10) / median;
        var microsecondsPerCall   = 1000 * median                        / callsPerSample;
        Console.WriteLine(
                          $"distance kernel {size}x{size} r={patchRadius}: median {median:F2} ms / {callsPerSample} calls " +
                          $"({microsecondsPerCall:F3} us/call, {callsPerSample / (median / 1000) / 1_000_000:F2} M calls/s) | " +
                          $"p10 {percentile10:F2} ms, p90 {percentile90:F2} ms, spread {interPercentileSpread:F0}% " +
                          $"(min {timings[0]:F2}, max {timings[^1]:F2}) " +
                          $"({samples} samples, checksum {checksums[0]}, stable: {checksums.All(c => c == checksums[0])})");
    }
}

static long RunDistanceSample(MaskedImage target, (int Y, int X)[] pairs, int patchRadius)
{
    long checksum = 0;
    foreach (var (y, x) in pairs)
    {
        var (otherY, otherX) = pairs[(y + x) % pairs.Length];
        checksum += PatchSsdDistanceMetric.CalculateImageDistance(target, y, x, target, otherY, otherX, patchRadius);
    }

    return checksum;
}

static void GenerateCorpus(string directory)
{
    // 合成 ground truth 语料：完整图作为真值，mask 位置抠成白色，供质量指标使用。
    // 真实漫画语料需要用户自行放入同一目录结构（image/mask/groundtruth 三件套）。
    Directory.CreateDirectory(directory);
    Console.WriteLine($"generating synthetic corpus in {directory}");
    var entries = new List<string>();

    foreach (var (typeName, generator) in new (string, Func<int, int, byte[]>)[]
             {
                 ("solid", (w,    h) => SyntheticImages.Solid(w, h, 17, 83, 149)),
                 ("gradient", (w, h) => SyntheticImages.Gradient(w, h)),
                 ("texture", (w,  h) => SyntheticImages.Textured(w, h, seed : 7)),
                 ("lines", (w,    h) => SyntheticImages.Lines(w, h)),
             })
    {
        const int size  = 512;
        var       image = generator(size, size);
        foreach (var (maskName, mask) in new[]
                 {
                     ("center-rect", Masks.CenterRectangle(size, size, 128, 128, 384, 384)),
                     ("scattered", Masks.ScatteredBlobs(size, size, seed : 42)),
                 })
        {
            var name     = $"{typeName}-{maskName}";
            var holey    = (byte[])image.Clone();
            var maskPath = Path.Combine(directory, name + ".mask.bin");
            for (var pixel = 0; pixel < mask.Length; pixel++)
            {
                if (mask[pixel] != 0)
                {
                    holey[pixel * 3]     = 255;
                    holey[pixel * 3 + 1] = 255;
                    holey[pixel * 3 + 2] = 255;
                }
            }

            File.WriteAllBytes(Path.Combine(directory, name + ".image.bin"), holey);
            File.WriteAllBytes(maskPath, mask);
            File.WriteAllBytes(Path.Combine(directory, name + ".truth.bin"), image);
            entries.Add(name);
        }
    }

    File.WriteAllLines(Path.Combine(directory, "manifest.txt"), entries);
    Console.WriteLine($"wrote {entries.Count} corpus entries (image/mask/truth per entry)");
}

static void RunRealCorpus(string directory)
{
    // 阶段 0 收尾：对真实漫画语料固化当前标量 CPU 的修复输出。PNG 仅供人工目检，
    // 字节级回归以 manifest 中的 SHA-256 为准（输出确定性由固定随机种子保证）。
    // Real-corpus closure for stage 0: freezes the current scalar CPU outputs. The PNGs
    // exist for eyeballing; the SHA-256 hashes in the manifest are the byte-level
    // regression reference (determinism comes from the fixed random seed).
    var outputDir = Path.Combine(directory, "baseline");
    Directory.CreateDirectory(outputDir);

    var options = new PatchMatchOptions { PatchRadius = 3, RandomSeed = 1212 };
    var originals = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                             .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                             .ToList();

    Console.WriteLine($"real corpus baseline over {originals.Count} top-level images in {directory}");
    Console.WriteLine($"output: {outputDir} (patch radius {options.PatchRadius}, seed {options.RandomSeed})");

    var manifest = new List<string>
    {
        $"# frozen {DateTime.Now:yyyy-MM-dd}; columns: name  size  coverage  elapsed-ms  sha256(output-rgb24)",
        $"patch-radius={options.PatchRadius}",
        $"random-seed={options.RandomSeed}",
    };
    var gallery = new StringBuilder();
    var total   = Stopwatch.StartNew();
    var ran     = 0;

    foreach (var file in originals)
    {
        var name     = Path.GetFileNameWithoutExtension(file);
        var maskPath = Path.Combine(directory, "candidates", name + ".candidate.mask.png");
        if (!File.Exists(maskPath))
        {
            Console.WriteLine($"  skip {name} (no candidate mask)");
            continue;
        }

        using var image     = Image.Load<Rgb24>(file);
        using var maskImage = Image.Load<L8>(maskPath);
        if (image.Width != maskImage.Width || image.Height != maskImage.Height)
        {
            Console.WriteLine($"  skip {name} (mask {maskImage.Width}x{maskImage.Height} != image {image.Width}x{image.Height})");
            continue;
        }

        var width     = image.Width;
        var height    = image.Height;
        var pixels    = new byte[width * height * 3];
        var maskBytes = new byte[width * height];
        image.CopyPixelDataTo(pixels);
        maskImage.CopyPixelDataTo(maskBytes);
        var mask  = new byte[width * height];
        var holes = 0;
        for (var pixel = 0; pixel < mask.Length; pixel++)
        {
            if (maskBytes[pixel] >= 128)
            {
                mask[pixel] = 1;
                holes++;
            }
        }

        var sw     = Stopwatch.StartNew();
        var output = PatchMatchInpainter.Inpaint(pixels, mask, width, height, options);
        sw.Stop();

        var hash = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
        using (var result = Image.LoadPixelData<Rgb24>(output, width, height))
        {
            result.SaveAsPng(Path.Combine(outputDir, name + ".inpaint.png"));
        }

        SaveSideBySide(pixels, output, width, height, Path.Combine(outputDir, name + ".compare.png"));

        var coverage = 100.0 * holes / (width * height);
        manifest.Add($"{name}\t{width}x{height}\t{coverage:F1}%\t{sw.Elapsed.TotalMilliseconds:F0}\t{hash}");
        gallery.AppendLine($"<img src=\"{name}.compare.png\">");
        gallery.AppendLine($"<p>{name} · {width}×{height} · 覆盖 {coverage:F1}% · {sw.Elapsed.TotalMilliseconds:F0} ms</p>");
        Console.WriteLine($"  {name,-48} {width,4}x{height,-4} {coverage,5:F1}%  {sw.Elapsed.TotalMilliseconds,7:F0} ms  {hash[..12]}");
        ran++;
    }

    total.Stop();
    File.WriteAllLines(Path.Combine(outputDir, "manifest.txt"), manifest);
    File.WriteAllText(Path.Combine(outputDir, "index.html"), BuildGalleryHtml(gallery.ToString(), ran, total.Elapsed));
    Console.WriteLine($"done: {ran} inpainted in {total.Elapsed.TotalSeconds:F1} s; manifest.txt + index.html in {outputDir}");
}

static void SaveSideBySide(byte[] original, byte[] inpainted, int width, int height, string path)
{
    // 左右对照：原图 | 6px 灰色间隔 | 修复结果。
    const int gap      = 6;
    var       rowWidth = width             * 2 + gap;
    var       compare  = new byte[rowWidth * height * 3];
    for (var y = 0; y < height; y++)
    {
        var src = y                 * width    * 3;
        var dst = y                 * rowWidth * 3;
        original.AsSpan(src, width  * 3).CopyTo(compare.AsSpan(dst));
        inpainted.AsSpan(src, width * 3).CopyTo(compare.AsSpan(dst + (width + gap) * 3));
        for (var x = 0; x < gap; x++)
        {
            var offset = dst + (width + x) * 3;
            compare[offset]     = 128;
            compare[offset + 1] = 128;
            compare[offset + 2] = 128;
        }
    }

    using var image = Image.LoadPixelData<Rgb24>(compare, rowWidth, height);
    image.SaveAsPng(path);
}

static string BuildGalleryHtml(string body, int count, TimeSpan elapsed)
{
    return $$"""
        <!doctype html>
        <html lang="zh">
        <meta charset="utf-8">
        <title>真实语料标量 CPU 基线</title>
        <style>
        body{font-family:system-ui,sans-serif;background:#1b1b1f;color:#ddd;max-width:1000px;margin:24px auto;padding:0 16px}
        h1{font-size:18px} p{color:#9aa;margin:2px 0 20px;font-size:13px}
        img{max-width:100%;display:block;border:1px solid #444}
        </style>
        <h1>真实语料标量 CPU 基线 — 左：原图，右：修复输出</h1>
        <p>{{count}} 张 · 共 {{elapsed.TotalSeconds:F1}} s · 参数与逐张 SHA-256 见 manifest.txt</p>
        {{body}}
        </html>
        """;
}

internal static class SyntheticImages
{
    internal static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var result = new byte[width * height * 3];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            result[pixel * 3]     = r;
            result[pixel * 3 + 1] = g;
            result[pixel * 3 + 2] = b;
        }

        return result;
    }

    internal static byte[] Gradient(int width, int height)
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

    internal static byte[] Textured(int width, int height, int seed)
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

    internal static byte[] Lines(int width, int height)
    {
        var result = Solid(width, height, 250, 250, 245);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                if (x % 37 is 0 or 1 || y % 53 is 0 or 1)
                {
                    result[offset]     = 20;
                    result[offset + 1] = 20;
                    result[offset + 2] = 30;
                }
            }
        }

        return result;
    }
}

internal static class Masks
{
    internal static byte[] CenterRectangle(int width, int height, int x0, int y0, int x1, int y1)
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

    internal static byte[] ScatteredBlobs(int width, int height, int seed)
    {
        var result = new byte[width * height];
        var random = new Random(seed);
        for (var blob = 0; blob < 24; blob++)
        {
            var centerX = random.Next(width);
            var centerY = random.Next(height);
            var radius  = random.Next(6, 24);
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
}

internal sealed class TimedDistanceMetric(IPatchDistanceMetric inner) : IPatchDistanceMetric
{
    public long Ticks;
    public long Calls;

    public int PatchRadius => inner.PatchRadius;

    public int Calculate(MaskedImage source, int sourceY, int sourceX, MaskedImage target, int targetY, int targetX)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var result    = inner.Calculate(source, sourceY, sourceX, target, targetY, targetX);
        Ticks += Stopwatch.GetTimestamp() - timestamp;
        Calls++;
        return result;
    }
}

internal readonly record struct PipelineProbeResult(
    double WallMilliseconds,
    double MetricMilliseconds,
    long   Calls,
    byte[] Output);
