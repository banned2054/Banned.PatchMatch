using System.Diagnostics;

namespace Banned.PatchMatch.Internal;

/// <summary>
/// 阶段 0 基线分析用的内部计时钩子。默认关闭；关闭时每次调用仅是一次静态布尔检查，
/// 不分配内存、不影响算法行为和输出。<br/>
/// Internal stage timing hooks for the stage-0 baseline profile. Disabled by default;
/// when disabled each call is a single static bool check with no allocation and no
/// effect on algorithm behavior or output.
/// </summary>
internal static class StageProfiler
{
    [ThreadStatic]
    private static List<Frame>? _frames;

    internal static bool Enabled;

    internal static readonly Dictionary<string, long> ElapsedTicks = [];

    internal static long Begin()
    {
        if (!Enabled)
        {
            return 0;
        }

        var start = Stopwatch.GetTimestamp();
        (_frames ??= []).Add(new Frame(start));
        return start;
    }

    internal static void End(string stage, long start)
    {
        if (!Enabled || start == 0)
        {
            return;
        }

        var frames = _frames;
        if (frames is null || frames.Count == 0)
        {
            throw new InvalidOperationException("Stage profiler scopes must be ended in LIFO order.");
        }

        var end        = Stopwatch.GetTimestamp();
        var frameIndex = frames.Count - 1;
        var frame      = frames[frameIndex];
        frames.RemoveAt(frameIndex);
        if (frame.Start != start)
        {
            throw new InvalidOperationException("Stage profiler scopes must be ended in LIFO order.");
        }

        var inclusiveTicks = end            - start;
        var exclusiveTicks = inclusiveTicks - frame.ChildTicks;
        ElapsedTicks.TryGetValue(stage, out var ticks);
        ElapsedTicks[stage] = ticks + exclusiveTicks;

        if (frames.Count > 0)
        {
            frameIndex = frames.Count - 1;
            var parent = frames[frameIndex];
            parent.ChildTicks  += inclusiveTicks;
            frames[frameIndex] =  parent;
        }
    }

    internal static void Reset()
    {
        ElapsedTicks.Clear();
        _frames?.Clear();
    }

    private struct Frame(long start)
    {
        internal long Start      = start;
        internal long ChildTicks = 0;
    }
}
