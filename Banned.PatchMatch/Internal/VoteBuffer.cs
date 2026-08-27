using System.Buffers;

namespace Banned.PatchMatch.Internal;

internal sealed class VoteBuffer : IDisposable
{
    private readonly int _length;

    private double[]? _values;

    internal VoteBuffer(int width, int height)
    {
        _length = checked(width * height * 4);
        _values = ArrayPool<double>.Shared.Rent(_length);
        Array.Clear(_values, 0, _length);
    }

    internal double[] Values => _values ?? throw new ObjectDisposedException(nameof(VoteBuffer));

    public void Dispose()
    {
        var values = Interlocked.Exchange(ref _values, null);
        if (values is not null)
        {
            ArrayPool<double>.Shared.Return(values);
        }
    }
}
