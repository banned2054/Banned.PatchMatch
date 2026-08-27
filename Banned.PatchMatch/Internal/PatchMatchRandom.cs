namespace Banned.PatchMatch.Internal;

/// <summary>
/// 实现 Microsoft C 运行时 rand 函数使用的确定性 15 位序列。每次操作使用独立实例，
/// 从而保证并发调用之间互不影响。<br/>
/// Implements the deterministic 15-bit sequence used by the Microsoft C runtime's rand function.
/// Keeping the generator local to one operation also makes concurrent calls independent.
/// </summary>
internal sealed class PatchMatchRandom(uint seed)
{
    private uint _state = seed;

    internal int Next(int maxExclusive)
    {
        _state = unchecked((_state * 214_013u) + 2_531_011u);
        var value = (int)((_state >> 16) & 0x7fff);
        return value % maxExclusive;
    }
}
