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

    public int PatchRadius { get; } = patchRadius;

    public int Calculate(MaskedImage source, int sourceY, int sourceX, MaskedImage target, int targetY, int targetX)
    {
        return CalculateImageDistance(source, sourceY, sourceX, target, targetY, targetX, PatchRadius);
    }

    internal static int CalculateImageDistance(MaskedImage source,  int sourceY, int sourceX, MaskedImage target,
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
