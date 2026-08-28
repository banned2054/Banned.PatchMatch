namespace Banned.PatchMatch.Internal;

internal sealed class InpaintingEngine
{
    private static readonly double[] DistanceToSimilarity = BuildDistanceToSimilarity();

    private readonly List<MaskedImage>    _pyramid = [];
    private readonly IPatchDistanceMetric _distanceMetric;
    private readonly PatchMatchRandom     _random;
    private readonly CancellationToken    _cancellationToken;

    private NearestNeighborField? _sourceToTarget;
    private NearestNeighborField? _targetToSource;

    internal InpaintingEngine(MaskedImage       initial, IPatchDistanceMetric distanceMetric, uint randomSeed,
                              CancellationToken cancellationToken)
    {
        _distanceMetric    = distanceMetric;
        _random            = new PatchMatchRandom(randomSeed);
        _cancellationToken = cancellationToken;
        InitializePyramid(initial);
    }

    internal byte[] Run()
    {
        MaskedImage? target = null;
        for (var level = _pyramid.Count - 1; level >= 0; level--)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var source = _pyramid[level];
            if (level == _pyramid.Count - 1)
            {
                target = source.Clone();
                target.ClearMask();
                _sourceToTarget = new NearestNeighborField(source, target, _distanceMetric, _random);
                _targetToSource = new NearestNeighborField(target, source, _distanceMetric, _random);
            }
            else
            {
                _sourceToTarget = new NearestNeighborField(source, target!, _distanceMetric, _random, _sourceToTarget!);
                _targetToSource = new NearestNeighborField(target!, source, _distanceMetric, _random, _targetToSource!);
            }

            target = ExpectationMaximization(source, target!, level);
        }

        return target!.Pixels;
    }

    private static double[] BuildDistanceToSimilarity()
    {
        double[] sample = [1.0, 0.99, 0.96, 0.83, 0.38, 0.11, 0.02, 0.005, 0.0006, 0.0001, 0];
        var      result = new double[PatchSsdDistanceMetric.DistanceScale + 1];
        for (var distance = 0; distance < result.Length; distance++)
        {
            var normalized = (double)distance / result.Length;
            var lowerIndex = (int)(100 * normalized);
            var upperIndex = lowerIndex + 1;
            var lower      = lowerIndex < sample.Length ? sample[lowerIndex] : 0;
            var upper      = upperIndex < sample.Length ? sample[upperIndex] : 0;
            result[distance] = lower + (((100 * normalized) - lowerIndex) * (upper - lower));
        }

        return result;
    }

    private void InitializePyramid(MaskedImage initial)
    {
        var source = initial;
        _pyramid.Add(source);
        while (source.Height > _distanceMetric.PatchRadius && source.Width > _distanceMetric.PatchRadius)
        {
            var timestamp = StageProfiler.Begin();
            source = source.Downsample();
            StageProfiler.End("downsample", timestamp);
            _pyramid.Add(source);
        }
    }

    private MaskedImage ExpectationMaximization(MaskedImage source, MaskedImage target, int level)
    {
        var          expectationMaximizationIterations = 1 + (2 * level);
        var          nearestNeighborIterations         = Math.Min(7, 1 + level);
        MaskedImage? newTarget                         = null;
        ulong        previousDistanceSourceToTarget    = 0;
        ulong        previousDistanceTargetToSource    = 0;

        for (var iteration = 0; iteration < expectationMaximizationIterations; iteration++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (iteration != 0)
            {
                _sourceToTarget!.SetTarget(newTarget!);
                _targetToSource!.SetSource(newTarget!);
                target = newTarget!;
            }

            SetIdentityLinks(source);

            var minimizeTimestamp = StageProfiler.Begin();
            var distanceSourceToTarget =
                _sourceToTarget!.Minimize(nearestNeighborIterations, true, true, _cancellationToken);
            var distanceTargetToSource =
                _targetToSource!.Minimize(nearestNeighborIterations, false, true, _cancellationToken);
            StageProfiler.End("nnf-minimize", minimizeTimestamp);

            // Ports the early-stopping heuristic from dmMaze's PyPatchMatchInpaint: stop the EM loop once
            // both nearest-neighbor fields stop improving, and jump straight to the upscaled vote.
            var isLastIteration = iteration == expectationMaximizationIterations - 1;
            var breakLoop       = false;
            if (level != 0 && !isLastIteration)
            {
                if (distanceSourceToTarget == 0 && distanceTargetToSource == 0)
                {
                    breakLoop = true;
                }
                else if (distanceSourceToTarget > 0 && distanceTargetToSource > 0 &&
                         previousDistanceSourceToTarget > 0 &&
                         (double)previousDistanceSourceToTarget / distanceSourceToTarget < 1.0001d &&
                         (double)previousDistanceTargetToSource / distanceTargetToSource < 1.0001d)
                {
                    // Only the source-to-target history is guarded, intentionally mirroring the reference
                    // DLL (dmMaze's PyPatchMatchInpaint checks distance_before_1 twice due to a typo that
                    // shipped in the released binary).
                    breakLoop = true;
                }
            }

            MaskedImage newSource;
            var         upscaled = breakLoop || (level >= 1 && isLastIteration);
            if (upscaled)
            {
                var upsampleTimestamp = StageProfiler.Begin();
                newSource = _pyramid[level - 1];
                newTarget = target.Upsample(newSource.Width, newSource.Height, newSource.GlobalMask);
                StageProfiler.End("upsample", upsampleTimestamp);
            }
            else
            {
                newSource = _pyramid[level];
                newTarget = target.Clone();
            }

            using var vote          = new VoteBuffer(newTarget.Width, newTarget.Height);
            var       voteTimestamp = StageProfiler.Begin();
            ExpectationStep(_sourceToTarget, true, vote.Values, newSource, newTarget, upscaled);
            ExpectationStep(_targetToSource, false, vote.Values, newSource, newTarget, upscaled);
            StageProfiler.End("vote", voteTimestamp);
            var maximizeTimestamp = StageProfiler.Begin();
            MaximizationStep(newTarget, vote.Values);
            StageProfiler.End("maximize", maximizeTimestamp);

            previousDistanceSourceToTarget = distanceSourceToTarget;
            previousDistanceTargetToSource = distanceTargetToSource;
            if (breakLoop)
            {
                break;
            }
        }

        return newTarget!;
    }

    private void SetIdentityLinks(MaskedImage source)
    {
        for (var y = 0; y < source.Height; y++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                SetIdentityLinkIfKnown(source, y, x);
            }
        }
    }

    private void SetIdentityLinkIfKnown(MaskedImage source, int y, int x)
    {
        if (source.ContainsMask(y, x, _distanceMetric.PatchRadius))
        {
            return;
        }

        _sourceToTarget!.SetIdentity(y, x);
        _targetToSource!.SetIdentity(y, x);
    }

    private void ExpectationStep(NearestNeighborField field, bool sourceToTarget, double[] votes, MaskedImage source,
                                 MaskedImage          destination, bool upscaled)
    {
        for (var y = 0; y < field.Source.Height; y++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < field.Source.Width; x++)
            {
                VoteForLink(field, sourceToTarget, votes, source, destination, upscaled, y, x);
            }
        }
    }

    private void VoteForLink(NearestNeighborField field,       bool sourceToTarget, double[] votes, MaskedImage source,
                             MaskedImage          destination, bool upscaled, int sourceCenterY, int sourceCenterX)
    {
        if (field.Source.IsGloballyMasked(sourceCenterY, sourceCenterX))
        {
            return;
        }

        // Ports the selective voting from dmMaze's PyPatchMatchInpaint: only hole pixels vote in the
        // source-to-target field and only non-hole pixels vote in the target-to-source field.
        if (sourceToTarget
                ? !field.Source.IsMasked(sourceCenterY, sourceCenterX)
                : field.Source.IsMasked(sourceCenterY, sourceCenterX))
        {
            return;
        }

        var matchedY    = field.Get(sourceCenterY, sourceCenterX, 0);
        var matchedX    = field.Get(sourceCenterY, sourceCenterX, 1);
        var distance    = field.Get(sourceCenterY, sourceCenterX, 2);
        var weight      = DistanceToSimilarity[Math.Clamp(distance, 0, PatchSsdDistanceMetric.DistanceScale)];
        var patchRadius = _distanceMetric.PatchRadius;
        for (var deltaY = -patchRadius; deltaY <= patchRadius; deltaY++)
        {
            for (var deltaX = -patchRadius; deltaX <= patchRadius; deltaX++)
            {
                var sourceY = sourceCenterY + deltaY;
                var sourceX = sourceCenterX + deltaX;
                var targetY = matchedY      + deltaY;
                var targetX = matchedX      + deltaX;
                if (!IsValidCorrespondence(field, sourceY, sourceX, targetY, targetX))
                {
                    continue;
                }

                if (!sourceToTarget)
                {
                    (sourceY, targetY) = (targetY, sourceY);
                    (sourceX, targetX) = (targetX, sourceX);
                }

                CopyVote(source, votes, destination, upscaled, sourceY, sourceX, targetY, targetX, weight);
            }
        }
    }

    private static bool IsValidCorrespondence(NearestNeighborField field, int sourceY, int sourceX, int targetY,
                                              int                  targetX)
    {
        return sourceY >= 0                                     && sourceY < field.Source.Height &&
               sourceX >= 0                                     && sourceX < field.Source.Width  &&
               !field.Source.IsGloballyMasked(sourceY, sourceX) &&
               targetY >= 0                                     && targetY < field.Target.Height &&
               targetX >= 0                                     && targetX < field.Target.Width  &&
               !field.Target.IsGloballyMasked(targetY, targetX);
    }

    private static void CopyVote(MaskedImage source,  double[] votes,   MaskedImage destination, bool upscaled,
                                 int         sourceY, int      sourceX, int         targetY, int targetX, double weight)
    {
        if (!upscaled)
        {
            WeightedCopy(source, sourceY, sourceX, votes, destination, targetY, targetX, weight);
            return;
        }

        for (var upsampleY = 0; upsampleY < 2; upsampleY++)
        {
            for (var upsampleX = 0; upsampleX < 2; upsampleX++)
            {
                WeightedCopy(source, (2 * sourceY) + upsampleY, (2 * sourceX) + upsampleX, votes, destination,
                             (2         * targetY) + upsampleY, (2 * targetX) + upsampleX, weight);
            }
        }
    }

    private static void WeightedCopy(MaskedImage source,      int sourceY, int sourceX, double[] votes,
                                     MaskedImage destination, int targetY, int targetX, double   weight)
    {
        if (sourceY < 0 || sourceY >= source.Height || sourceX < 0 || sourceX >= source.Width ||
            targetY < 0 || targetY >= destination.Height || targetX < 0 || targetX >= destination.Width ||
            source.IsMasked(sourceY, sourceX) || source.IsGloballyMasked(sourceY, sourceX))
        {
            return;
        }

        var sourceOffset = ((sourceY * source.Width)      + sourceX) * 3;
        var voteOffset   = ((targetY * destination.Width) + targetX) * 4;
        votes[voteOffset]     += source.Pixels[sourceOffset]     * weight;
        votes[voteOffset + 1] += source.Pixels[sourceOffset + 1] * weight;
        votes[voteOffset + 2] += source.Pixels[sourceOffset + 2] * weight;
        votes[voteOffset + 3] += weight;
    }

    private void MaximizationStep(MaskedImage target, double[] votes)
    {
        for (var y = 0; y < target.Height; y++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < target.Width; x++)
            {
                if (target.IsGloballyMasked(y, x))
                {
                    continue;
                }

                var pixel      = (y * target.Width) + x;
                var voteOffset = pixel * 4;
                var weight     = votes[voteOffset + 3];
                if (weight > 0)
                {
                    var targetOffset = pixel * 3;
                    target.Pixels[targetOffset]     = SaturateToByte(votes[voteOffset]     / weight);
                    target.Pixels[targetOffset + 1] = SaturateToByte(votes[voteOffset + 1] / weight);
                    target.Pixels[targetOffset + 2] = SaturateToByte(votes[voteOffset + 2] / weight);
                }
                else
                {
                    target.SetMask(y, x, false);
                }
            }
        }

        // 像素已被原地改写，特征缓冲随之失效，下一轮距离计算前会按当前像素重建。
        target.InvalidateFeatures();
    }

    private static byte SaturateToByte(double value)
    {
        return (byte)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), byte.MinValue, byte.MaxValue);
    }
}
