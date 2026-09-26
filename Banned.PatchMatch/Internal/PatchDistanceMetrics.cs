using System.Numerics;
using System.Runtime.Intrinsics;

namespace Banned.PatchMatch.Internal;

internal interface IPatchDistanceMetric
{
    int PatchRadius { get; }

    int Calculate(MaskedImage source, int sourceY, int sourceX, MaskedImage target, int targetY, int targetX);
}

internal sealed class PatchSsdDistanceMetric(int patchRadius) : IPatchDistanceMetric
{
    internal const int DistanceScale = 65_535;
    private const  int SsdScale      = 9 * 255 * 255;

    // 阶段 1 原型的路径开关，仅供测试与基准在同一进程内切换路径；运行期并发翻转不受支持。
    // Phase-1 prototype path switches for tests and benchmarks; flipping them while
    // distance calls are in flight is unsupported.
    internal static bool ForceScalar;
    internal static bool UseFeatureLayout = true;
    internal static int? ForceVectorBytes;

    // uint 累加通道的无回绕上界：按特征布局每像素 12 字节（含零填充）保守计算，
    // 最窄的 Vector128 每通道至多累计 floor(12(2r+1)²/16) 个平方项，每项 ≤ 255²。
    // r ≤ 127 时上界约为 3.2×10⁹，小于 2³²；归约前必须扩展为 ulong 再合并通道。
    private const int MaxVectorPatchRadius = 127;

    private static readonly int AutoVectorBytes =
        Vector512.IsHardwareAccelerated ? 64 : Vector256.IsHardwareAccelerated ? 32 : 16;

    public int PatchRadius { get; } = patchRadius;

    public int Calculate(MaskedImage source, int sourceY, int sourceX, MaskedImage target, int targetY, int targetX)
    {
        return CalculateImageDistance(source, sourceY, sourceX, target, targetY, targetX, PatchRadius);
    }

    internal static int CalculateImageDistance(MaskedImage source,  int sourceY, int sourceX, MaskedImage target,
                                               int         targetY, int targetX, int patchRadius)
    {
        if (ForceScalar || patchRadius > MaxVectorPatchRadius || !Vector128.IsHardwareAccelerated)
        {
            return CalculateImageDistanceScalar(source, sourceY, sourceX, target, targetY, targetX, patchRadius);
        }

        // 分派点收敛在此单一方法内，ISA 分支不扩散到算法其余部分。
        var vectorBytes = ForceVectorBytes ?? AutoVectorBytes;
        if (UseFeatureLayout)
        {
            return vectorBytes >= 64
                ? CalculateFeaturesCore<Vector512SsdSink>(source, sourceY, sourceX, target, targetY, targetX,
                                                          patchRadius)
                : vectorBytes >= 32
                    ? CalculateFeaturesCore<Vector256SsdSink>(source, sourceY, sourceX, target, targetY, targetX,
                                                              patchRadius)
                    : CalculateFeaturesCore<Vector128SsdSink>(source, sourceY, sourceX, target, targetY, targetX,
                                                              patchRadius);
        }

        return vectorBytes >= 64
            ? CalculateDirectCore<Vector512SsdSink>(source, sourceY, sourceX, target, targetY, targetX, patchRadius)
            : vectorBytes >= 32
                ? CalculateDirectCore<Vector256SsdSink>(source, sourceY, sourceX, target, targetY, targetX, patchRadius)
                : CalculateDirectCore<Vector128SsdSink>(source, sourceY, sourceX, target, targetY, targetX,
                                                        patchRadius);
    }

    /// <summary>
    /// 标量参考实现：阶段 0 冻结的原版内核，逐字节保持不变，兼作无向量平台的运行时兜底
    /// 与差分测试的基准。<br/>
    /// Scalar reference: the stage-0 frozen kernel kept verbatim, serving as the runtime
    /// fallback on machines without vectors and as the differential-testing baseline.
    /// </summary>
    internal static int CalculateImageDistanceScalar(MaskedImage source,  int sourceY, int sourceX, MaskedImage target,
                                                     int         targetY, int targetX, int patchRadius)
    {
        double distance  = 0;
        double weightSum = 0;

        var sourceGradientY = source.GradientY;
        var sourceGradientX = source.GradientX;
        var targetGradientY = target.GradientY;
        var targetGradientX = target.GradientX;
        var patchWidth      = (2 * patchRadius) + 1;

        for (var deltaY = -patchRadius; deltaY <= patchRadius; deltaY++)
        {
            var sourcePatchY = sourceY + deltaY;
            var targetPatchY = targetY + deltaY;

            if (sourcePatchY <= 0 || sourcePatchY >= source.Height - 1 ||
                targetPatchY <= 0 || targetPatchY >= target.Height - 1)
            {
                distance  += (double)SsdScale * patchWidth;
                weightSum += patchWidth;
                continue;
            }

            var sourceMaskRow  = sourcePatchY  * source.Width;
            var targetMaskRow  = targetPatchY  * target.Width;
            var sourcePixelRow = sourceMaskRow * 3;
            var targetPixelRow = targetMaskRow * 3;

            for (var deltaX = -patchRadius; deltaX <= patchRadius; deltaX++)
            {
                var sourcePatchX = sourceX + deltaX;
                var targetPatchX = targetX + deltaX;
                weightSum++;

                if (sourcePatchX <= 0 || sourcePatchX >= source.Width - 1 ||
                    targetPatchX <= 0 || targetPatchX >= target.Width - 1)
                {
                    distance += SsdScale;
                    continue;
                }

                var sourceMaskOffset = sourceMaskRow + sourcePatchX;
                var targetMaskOffset = targetMaskRow + targetPatchX;
                if (source.Mask[sourceMaskOffset] != 0                                        ||
                    target.Mask[targetMaskOffset] != 0                                        ||
                    source.GlobalMask is not null && source.GlobalMask[sourceMaskOffset] != 0 ||
                    target.GlobalMask is not null && target.GlobalMask[targetMaskOffset] != 0)
                {
                    distance += SsdScale;
                    continue;
                }

                var sourceOffset = sourcePixelRow + (sourcePatchX * 3);
                var targetOffset = targetPixelRow + (targetPatchX * 3);
                var ssd          = 0;
                for (var channel = 0; channel < 3; channel++)
                {
                    ssd += Square(source.Pixels[sourceOffset   + channel] - target.Pixels[targetOffset   + channel]);
                    ssd += Square(sourceGradientX[sourceOffset + channel] - targetGradientX[targetOffset + channel]);
                    ssd += Square(sourceGradientY[sourceOffset + channel] - targetGradientY[targetOffset + channel]);
                }

                distance += ssd;
            }
        }

        distance /= SsdScale;
        var result = (int)(DistanceScale * distance / weightSum);
        return result is < 0 or > DistanceScale ? DistanceScale : result;
    }

    private static int Square(int value)
    {
        return value * value;
    }

    private static int CalculateFeaturesCore<TSink>(MaskedImage source,  int sourceY, int sourceX, MaskedImage target,
                                                    int         targetY, int targetX, int patchRadius)
        where TSink : struct, ISsdSink
    {
        // 特征布局：每像素 12 字节交织（9 有效 + 3 零填充），一个有效行程成为单一连续
        // 区间；两侧填充相同，对 SSD 贡献 0。
        var  sourceFeatures = source.Features;
        var  targetFeatures = target.Features;
        var  sourceInvalid  = source.InvalidBits;
        var  targetInvalid  = target.InvalidBits;
        var  sink           = default(TSink);
        long validPixels    = 0;

        for (var deltaY = -patchRadius; deltaY <= patchRadius; deltaY++)
        {
            var sourcePatchY = sourceY + deltaY;
            var targetPatchY = targetY + deltaY;
            if (sourcePatchY <= 0 || sourcePatchY >= source.Height - 1 ||
                targetPatchY <= 0 || targetPatchY >= target.Height - 1)
            {
                continue;
            }

            var deltaXLow  = Math.Max(-patchRadius, Math.Max(1           - sourceX, 1 - targetX));
            var deltaXHigh = Math.Min(patchRadius, Math.Min(source.Width - 2 - sourceX, target.Width - 2 - targetX));
            if (deltaXLow > deltaXHigh)
            {
                continue;
            }

            var sourceRow = sourcePatchY * source.Width;
            var targetRow = targetPatchY * target.Width;
            for (var deltaX = deltaXLow; deltaX <= deltaXHigh;)
            {
                var count = (int)Math.Min(64, deltaXHigh - deltaX + 1);
                var bits = RowValidBits(sourceInvalid, sourceRow + sourceX + deltaX, targetInvalid,
                                        targetRow                + targetX + deltaX, count);
                while (bits != 0)
                {
                    var lead      = BitOperations.TrailingZeroCount(bits);
                    var runLength = BitOperations.TrailingZeroCount(~(bits >> lead));
                    var runStart  = sourceX + deltaX + lead;
                    var runTarget = targetX + deltaX + lead;
                    sink.Add(sourceFeatures.AsSpan((sourceRow + runStart) * MaskedImage.FeatureStride,
                                                   runLength              * MaskedImage.FeatureStride),
                             targetFeatures.AsSpan((targetRow + runTarget) * MaskedImage.FeatureStride,
                                                   runLength               * MaskedImage.FeatureStride));
                    validPixels += runLength;
                    bits        &= ~((runLength == 64 ? ulong.MaxValue : ((1ul << runLength) - 1)) << lead);
                }

                deltaX += count;
            }
        }

        return FinishDistance(sink.Total(), validPixels, patchRadius);
    }

    private static int CalculateDirectCore<TSink>(MaskedImage source,  int sourceY, int sourceX, MaskedImage target,
                                                  int         targetY, int targetX, int patchRadius)
        where TSink : struct, ISsdSink
    {
        // 现有布局：像素与两份梯度数组分离，每个有效行程送三对连续区间。
        var  sourcePixels    = source.Pixels;
        var  targetPixels    = target.Pixels;
        var  sourceGradientX = source.GradientX;
        var  sourceGradientY = source.GradientY;
        var  targetGradientX = target.GradientX;
        var  targetGradientY = target.GradientY;
        var  sourceInvalid   = source.InvalidBits;
        var  targetInvalid   = target.InvalidBits;
        var  sink            = default(TSink);
        long validPixels     = 0;

        for (var deltaY = -patchRadius; deltaY <= patchRadius; deltaY++)
        {
            var sourcePatchY = sourceY + deltaY;
            var targetPatchY = targetY + deltaY;
            if (sourcePatchY <= 0 || sourcePatchY >= source.Height - 1 ||
                targetPatchY <= 0 || targetPatchY >= target.Height - 1)
            {
                continue;
            }

            var deltaXLow  = Math.Max(-patchRadius, Math.Max(1           - sourceX, 1 - targetX));
            var deltaXHigh = Math.Min(patchRadius, Math.Min(source.Width - 2 - sourceX, target.Width - 2 - targetX));
            if (deltaXLow > deltaXHigh)
            {
                continue;
            }

            var sourceRow = sourcePatchY * source.Width;
            var targetRow = targetPatchY * target.Width;
            for (var deltaX = deltaXLow; deltaX <= deltaXHigh;)
            {
                var count = (int)Math.Min(64, deltaXHigh - deltaX + 1);
                var bits = RowValidBits(sourceInvalid, sourceRow + sourceX + deltaX, targetInvalid,
                                        targetRow                + targetX + deltaX, count);
                while (bits != 0)
                {
                    var lead        = BitOperations.TrailingZeroCount(bits);
                    var runLength   = BitOperations.TrailingZeroCount(~(bits >> lead));
                    var byteCount   = runLength                             * 3;
                    var sourceStart = (sourceRow + sourceX + deltaX + lead) * 3;
                    var targetStart = (targetRow + targetX + deltaX + lead) * 3;
                    sink.Add(sourcePixels.AsSpan(sourceStart, byteCount), targetPixels.AsSpan(targetStart, byteCount));
                    sink.Add(sourceGradientX.AsSpan(sourceStart, byteCount),
                             targetGradientX.AsSpan(targetStart, byteCount));
                    sink.Add(sourceGradientY.AsSpan(sourceStart, byteCount),
                             targetGradientY.AsSpan(targetStart, byteCount));
                    validPixels += runLength;
                    bits        &= ~((runLength == 64 ? ulong.MaxValue : ((1ul << runLength) - 1)) << lead);
                }

                deltaX += count;
            }
        }

        return FinishDistance(sink.Total(), validPixels, patchRadius);
    }

    private static ulong RowValidBits(ulong[] sourceInvalid, int sourceBase, ulong[] targetInvalid, int targetBase,
                                      int     count)
    {
        var invalid = ExtractBits(sourceInvalid, sourceBase) | ExtractBits(targetInvalid, targetBase);
        var mask    = count == 64 ? ulong.MaxValue : (1ul << count) - 1;
        return ~invalid & mask;
    }

    private static ulong ExtractBits(ulong[] words, int bitOffset)
    {
        var wordIndex = bitOffset >> 6;
        var shift     = bitOffset & 63;
        var result    = words[wordIndex] >> shift;
        if (shift != 0 && wordIndex + 1 < words.Length)
        {
            result |= words[wordIndex + 1] << (64 - shift);
        }

        return result;
    }

    private static int FinishDistance(long squaredSum, long validPixels, int patchRadius)
    {
        // 计数式重构：weightSum 恒等于 patchWidth²（原实现逐像素累加的等价结果），
        // 距离 = SsdScale×(P²-有效像素数) + Σssd。全部为整数运算，double 只是精确容器，
        // 因此重构与原实现的求和顺序差异不影响结果。
        var    patchPixels = (long)(2 * patchRadius  + 1) * (2 * patchRadius + 1);
        double distance    = SsdScale * (patchPixels - validPixels) + squaredSum;
        distance /= SsdScale;
        var result = (int)(DistanceScale * distance / patchPixels);
        return result is < 0 or > DistanceScale ? DistanceScale : result;
    }
}

internal sealed class RegularityGuidedDistanceMetric(
    int     patchRadius,
    float[] guideMap,
    int     guideWidth,
    int     guideHeight,
    int     guideChannels,
    float   guideWeight) : IPatchDistanceMetric
{
    private const double MaximumPeriodicDistance = 0.707;

    public int PatchRadius { get; } = patchRadius;

    public int Calculate(MaskedImage source, int sourceY, int sourceX, MaskedImage target, int targetY, int targetX)
    {
        if (targetY < 0 || targetY >= target.Height || targetX < 0 || targetX >= target.Width)
        {
            return PatchSsdDistanceMetric.DistanceScale;
        }

        var regularityScore = (double)PatchSsdDistanceMetric.DistanceScale;
        if (!source.IsGloballyMasked(sourceY, sourceX) && !target.IsGloballyMasked(targetY, targetX))
        {
            var sourceScale  = Math.Max(1, guideHeight / source.Height);
            var targetScale  = Math.Max(1, guideHeight / target.Height);
            var sourceGuideY = Math.Min(sourceY        * sourceScale, guideHeight - 1);
            var sourceGuideX = Math.Min(sourceX        * sourceScale, guideWidth  - 1);
            var targetGuideY = Math.Min(targetY        * targetScale, guideHeight - 1);
            var targetGuideX = Math.Min(targetX        * targetScale, guideWidth  - 1);
            var sourceOffset = ((sourceGuideY * guideWidth) + sourceGuideX) * guideChannels;
            var targetOffset = ((targetGuideY * guideWidth) + targetGuideX) * guideChannels;

            var coordinate0Distance = Math.Abs(guideMap[sourceOffset]     - guideMap[targetOffset]);
            var coordinate1Distance = Math.Abs(guideMap[sourceOffset + 1] - guideMap[targetOffset + 1]);
            if (coordinate0Distance > 0.5f)
            {
                coordinate0Distance = 1 - coordinate0Distance;
            }

            if (coordinate1Distance > 0.5f)
            {
                coordinate1Distance = 1 - coordinate1Distance;
            }

            regularityScore =
                Math.Sqrt(coordinate0Distance * coordinate0Distance + coordinate1Distance * coordinate1Distance) /
                MaximumPeriodicDistance;
            regularityScore = Math.Clamp(regularityScore, 0, 1) * PatchSsdDistanceMetric.DistanceScale;
        }

        var imageScore =
            PatchSsdDistanceMetric.CalculateImageDistance(source, sourceY, sourceX, target, targetY, targetX,
                                                          PatchRadius);
        return (int)(((regularityScore * guideWeight) + imageScore) / (1 + guideWeight));
    }
}
