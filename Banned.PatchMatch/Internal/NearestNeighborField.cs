namespace Banned.PatchMatch.Internal;

internal sealed class NearestNeighborField
{
    private const int MaximumInitializationAttempts = 20;

    private readonly int[] _field;

    private readonly IPatchDistanceMetric _distanceMetric;
    private readonly PatchMatchRandom     _random;

    internal NearestNeighborField(MaskedImage      source, MaskedImage target, IPatchDistanceMetric distanceMetric,
                                  PatchMatchRandom random)
    {
        Source          = source;
        Target          = target;
        _distanceMetric = distanceMetric;
        _random         = random;
        _field          = new int[checked(source.Width * source.Height * 3)];
        var timestamp = StageProfiler.Begin();
        RandomizeField(MaximumInitializationAttempts, true);
        StageProfiler.End("nnf-init", timestamp);
    }

    internal NearestNeighborField(MaskedImage      source, MaskedImage target, IPatchDistanceMetric distanceMetric,
                                  PatchMatchRandom random, NearestNeighborField previous)
    {
        Source          = source;
        Target          = target;
        _distanceMetric = distanceMetric;
        _random         = random;
        _field          = new int[checked(source.Width * source.Height * 3)];
        var timestamp = StageProfiler.Begin();
        InitializeFieldFrom(previous, MaximumInitializationAttempts);
        StageProfiler.End("nnf-init", timestamp);
    }

    internal MaskedImage Source { get; private set; }

    internal MaskedImage Target { get; private set; }

    internal void SetSource(MaskedImage source)
    {
        Source = source;
    }

    internal void SetTarget(MaskedImage target)
    {
        Target = target;
    }

    internal int Get(int y, int x, int channel)
    {
        return _field[Offset(y, x) + channel];
    }

    internal void SetIdentity(int y, int x)
    {
        var offset = Offset(y, x);
        _field[offset]     = y;
        _field[offset + 1] = x;
        _field[offset + 2] = 0;
    }

    /// <summary>
    /// 最小化最近邻场并返回收敛距离。sourceToTarget 场仅对孔洞像素最小化，
    /// targetToSource 场仅对非孔洞像素最小化，与 dmMaze 的 PyPatchMatchInpaint 行为一致。<br/>
    /// Minimizes the nearest-neighbor field and returns the accumulated convergence distance.
    /// The source-to-target field only minimizes links for hole pixels while the target-to-source
    /// field only minimizes links for non-hole pixels, matching dmMaze's PyPatchMatchInpaint.
    /// </summary>
    internal ulong Minimize(int               passCount, bool sourceToTarget, bool conditionalSkip,
                            CancellationToken cancellationToken)
    {
        ulong totalDistance = 0;
        while (passCount-- > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalDistance += SweepForward(sourceToTarget, conditionalSkip, cancellationToken);
            totalDistance += SweepBackward(sourceToTarget, conditionalSkip, cancellationToken);
        }

        return totalDistance;
    }

    private ulong SweepForward(bool sourceToTarget, bool conditionalSkip, CancellationToken cancellationToken)
    {
        ulong totalDistance = 0;
        for (var y = 0; y < Source.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < Source.Width; x++)
            {
                totalDistance += MinimizeLinkIfNeeded(y, x, 1, sourceToTarget, conditionalSkip);
            }
        }

        return totalDistance;
    }

    private ulong SweepBackward(bool sourceToTarget, bool conditionalSkip, CancellationToken cancellationToken)
    {
        ulong totalDistance = 0;
        for (var y = Source.Height - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = Source.Width - 1; x >= 0; x--)
            {
                totalDistance += MinimizeLinkIfNeeded(y, x, -1, sourceToTarget, conditionalSkip);
            }
        }

        return totalDistance;
    }

    private ulong MinimizeLinkIfNeeded(int y, int x, int direction, bool sourceToTarget, bool conditionalSkip)
    {
        if (Source.IsGloballyMasked(y, x))
        {
            return 0;
        }

        if (conditionalSkip && (sourceToTarget ? !Source.IsMasked(y, x) : Source.IsMasked(y, x)))
        {
            return 0;
        }

        if (Get(y, x, 2) <= 0)
        {
            return 0;
        }

        MinimizeLink(y, x, direction);
        var distance = Get(y, x, 2);
        return distance > 0 ? (ulong)distance : 0;
    }

    private int Offset(int y, int x)
    {
        return ((y * Source.Width) + x) * 3;
    }

    private void RandomizeField(int maximumAttempts, bool reset)
    {
        for (var y = 0; y < Source.Height; y++)
        {
            for (var x = 0; x < Source.Width; x++)
            {
                RandomizeLink(y, x, maximumAttempts, reset);
            }
        }
    }

    private void RandomizeLink(int y, int x, int maximumAttempts, bool reset)
    {
        if (Source.IsGloballyMasked(y, x))
        {
            return;
        }

        var offset   = Offset(y, x);
        var distance = reset ? PatchSsdDistanceMetric.DistanceScale : _field[offset + 2];
        if (distance < PatchSsdDistanceMetric.DistanceScale)
        {
            return;
        }

        var targetY = 0;
        var targetX = 0;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            targetY = _random.Next(Source.Height);
            targetX = _random.Next(Source.Width);
            if (Target.IsGloballyMasked(targetY, targetX))
            {
                continue;
            }

            distance = CalculateDistance(y, x, targetY, targetX);
            if (distance < PatchSsdDistanceMetric.DistanceScale)
            {
                break;
            }
        }

        _field[offset]     = targetY;
        _field[offset + 1] = targetX;
        _field[offset + 2] = distance;
    }

    private void InitializeFieldFrom(NearestNeighborField previous, int maximumAttempts)
    {
        var verticalScale   = (double)Source.Height / previous.Source.Height;
        var horizontalScale = (double)Source.Width  / previous.Source.Width;

        for (var y = 0; y < Source.Height; y++)
        {
            for (var x = 0; x < Source.Width; x++)
            {
                if (Source.IsGloballyMasked(y, x))
                {
                    continue;
                }

                var previousY = (int)Math.Min(y / verticalScale, previous.Source.Height  - 1d);
                var previousX = (int)Math.Min(x / horizontalScale, previous.Source.Width - 1d);
                var offset    = Offset(y, x);
                var targetY   = (int)(previous.Get(previousY, previousX, 0) * verticalScale);
                var targetX   = (int)(previous.Get(previousY, previousX, 1) * horizontalScale);
                _field[offset]     = targetY;
                _field[offset + 1] = targetX;
                _field[offset + 2] = CalculateDistance(y, x, targetY, targetX);
            }
        }

        RandomizeField(maximumAttempts, false);
    }

    private void MinimizeLink(int y, int x, int direction)
    {
        var offset = Offset(y, x);

        var neighborY = y - direction;
        if (neighborY >= 0 && neighborY < Source.Height && !Source.IsGloballyMasked(neighborY, x))
        {
            var targetY = Get(neighborY, x, 0) + direction;
            var targetX = Get(neighborY, x, 1);
            TryUpdate(offset, y, x, targetY, targetX);
        }

        var neighborX = x - direction;
        if (neighborX >= 0 && neighborX < Source.Width && !Source.IsGloballyMasked(y, neighborX))
        {
            var targetY = Get(y, neighborX, 0);
            var targetX = Get(y, neighborX, 1) + direction;
            TryUpdate(offset, y, x, targetY, targetX);
        }

        var randomScale = (Math.Min(Target.Height, Target.Width) - 1) / 2;
        while (randomScale > 0)
        {
            var targetY = _field[offset] + _random.Next((2 * randomScale) + 1) - randomScale;
            var targetX = _field[offset + 1] + _random.Next((2 * randomScale) + 1) - randomScale;
            targetY = Math.Clamp(targetY, 0, Target.Height - 1);
            targetX = Math.Clamp(targetX, 0, Target.Width - 1);

            if (Target.IsGloballyMasked(targetY, targetX))
            {
                randomScale /= 2;
            }

            TryUpdate(offset, y, x, targetY, targetX);
            randomScale /= 2;
        }
    }

    private void TryUpdate(int offset, int sourceY, int sourceX, int targetY, int targetX)
    {
        var distance = CalculateDistance(sourceY, sourceX, targetY, targetX);
        if (distance >= _field[offset + 2])
        {
            return;
        }

        _field[offset]     = targetY;
        _field[offset + 1] = targetX;
        _field[offset + 2] = distance;
    }

    private int CalculateDistance(int sourceY, int sourceX, int targetY, int targetX)
    {
        return _distanceMetric.Calculate(Source, sourceY, sourceX, Target, targetY, targetX);
    }
}
