using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Banned.PatchMatch.Internal;

/// <summary>
/// 对两条等长字节区间累加平方差和。所有累加均为整数，与求和顺序无关，因此向量主体、
/// 窄向量尾部与标量尾部的任意组合都不会改变结果。调用方需保证区间等长，且单次内核
/// 调用的总字节数满足累加器的 uint 溢出上界（见 PatchSsdDistanceMetric 的半径守卫）。<br/>
/// Accumulates the sum of squared differences over two equal-length byte spans. All arithmetic
/// is integral and order-independent, so any mix of the vector body, the narrower-vector tail
/// and the scalar tail yields the same total. Callers must pass spans of equal length and keep
/// the per-call byte volume within the uint accumulator bound (see the radius guard in
/// PatchSsdDistanceMetric).
/// </summary>
internal interface ISsdSink
{
    void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    long Total();
}

internal struct ScalarSsdSink : ISsdSink
{
    private long _sum;

    public void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var sum = _sum;
        for (var i = 0; i < a.Length; i++)
        {
            var difference = a[i] - b[i];
            sum += (long)difference * difference;
        }

        _sum = sum;
    }

    public long Total() => _sum;
}

internal struct Vector128SsdSink : ISsdSink
{
    private Vector128<uint> _accumulator0;
    private Vector128<uint> _accumulator1;
    private Vector128<uint> _accumulator2;
    private Vector128<uint> _accumulator3;
    private ScalarSsdSink   _tail;

    public void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = a.Length;
        var i      = 0;
        for (var limit = length & ~15; i < limit; i += 16)
        {
            Accumulate(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i),
                       Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i));
        }

        _tail.Add(a[i..], b[i..]);
    }

    public long Total()
    {
        var        sum   = _accumulator0 + _accumulator1 + _accumulator2 + _accumulator3;
        Span<uint> lanes = stackalloc uint[Vector128<uint>.Count];
        sum.CopyTo(lanes);
        var total = _tail.Total();
        foreach (var lane in lanes)
        {
            total += lane;
        }

        return total;
    }

    private void Accumulate(Vector128<byte> va, Vector128<byte> vb)
    {
        // 字节域 |a-b|：Max-Min 不会回绕；平方最大 255²=65025，恰好落在 ushort 表示域内。
        var difference = Vector128.Max(va, vb) - Vector128.Min(va, vb);
        var (low, high)        =  Vector128.Widen(difference);
        var (square0, square1) =  Vector128.Widen(low  * low);
        var (square2, square3) =  Vector128.Widen(high * high);
        _accumulator0          += square0;
        _accumulator1          += square1;
        _accumulator2          += square2;
        _accumulator3          += square3;
    }
}

internal struct Vector256SsdSink : ISsdSink
{
    private Vector256<uint>  _accumulator0;
    private Vector256<uint>  _accumulator1;
    private Vector256<uint>  _accumulator2;
    private Vector256<uint>  _accumulator3;
    private Vector128SsdSink _tail;

    public void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = a.Length;
        var i      = 0;
        for (var limit = length & ~31; i < limit; i += 32)
        {
            Accumulate(Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i),
                       Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i));
        }

        _tail.Add(a[i..], b[i..]);
    }

    public long Total()
    {
        var        sum   = _accumulator0 + _accumulator1 + _accumulator2 + _accumulator3;
        Span<uint> lanes = stackalloc uint[Vector256<uint>.Count];
        sum.CopyTo(lanes);
        var total = _tail.Total();
        foreach (var lane in lanes)
        {
            total += lane;
        }

        return total;
    }

    private void Accumulate(Vector256<byte> va, Vector256<byte> vb)
    {
        var difference = Vector256.Max(va, vb) - Vector256.Min(va, vb);
        var (low, high)        =  Vector256.Widen(difference);
        var (square0, square1) =  Vector256.Widen(low  * low);
        var (square2, square3) =  Vector256.Widen(high * high);
        _accumulator0          += square0;
        _accumulator1          += square1;
        _accumulator2          += square2;
        _accumulator3          += square3;
    }
}

internal struct Vector512SsdSink : ISsdSink
{
    private Vector512<uint>  _accumulator0;
    private Vector512<uint>  _accumulator1;
    private Vector512<uint>  _accumulator2;
    private Vector512<uint>  _accumulator3;
    private Vector256SsdSink _tail;

    public void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = a.Length;
        var i      = 0;
        for (var limit = length & ~63; i < limit; i += 64)
        {
            Accumulate(Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i),
                       Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i));
        }

        _tail.Add(a[i..], b[i..]);
    }

    public long Total()
    {
        var        sum   = _accumulator0 + _accumulator1 + _accumulator2 + _accumulator3;
        Span<uint> lanes = stackalloc uint[Vector512<uint>.Count];
        sum.CopyTo(lanes);
        var total = _tail.Total();
        foreach (var lane in lanes)
        {
            total += lane;
        }

        return total;
    }

    private void Accumulate(Vector512<byte> va, Vector512<byte> vb)
    {
        var difference = Vector512.Max(va, vb) - Vector512.Min(va, vb);
        var (low, high)        =  Vector512.Widen(difference);
        var (square0, square1) =  Vector512.Widen(low  * low);
        var (square2, square3) =  Vector512.Widen(high * high);
        _accumulator0          += square0;
        _accumulator1          += square1;
        _accumulator2          += square2;
        _accumulator3          += square3;
    }
}
