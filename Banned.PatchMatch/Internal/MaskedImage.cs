namespace Banned.PatchMatch.Internal;

internal sealed class MaskedImage
{
    private static readonly int[] DownsampleKernel = [1, 5, 10, 10, 5, 1];

    private byte[]? _gradientY;
    private byte[]? _gradientX;
    private bool    _gradientsComputed;

    private MaskedImage(int width, int height, byte[] pixels, byte[] mask, byte[]? globalMask, byte[]? gradientY = null,
                        byte[]? gradientX = null, bool gradientsComputed = false)
    {
        Width              = width;
        Height             = height;
        Pixels             = pixels;
        Mask               = mask;
        GlobalMask         = globalMask;
        _gradientY         = gradientY;
        _gradientX         = gradientX;
        _gradientsComputed = gradientsComputed;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal byte[] Pixels { get; }

    internal byte[] Mask { get; }

    internal byte[]? GlobalMask { get; private set; }

    internal byte[] GradientY
    {
        get
        {
            EnsureGradients();
            return _gradientY!;
        }
    }

    internal byte[] GradientX
    {
        get
        {
            EnsureGradients();
            return _gradientX!;
        }
    }

    internal static MaskedImage Create(ReadOnlySpan<byte> pixels,     ReadOnlySpan<byte> mask,
                                       ReadOnlySpan<byte> globalMask, int                width, int height)
    {
        return new MaskedImage(width, height, pixels.ToArray(), mask.ToArray(),
                               globalMask.IsEmpty ? null : globalMask.ToArray());
    }

    private static MaskedImage CreateEmpty(int width, int height, bool hasGlobalMask)
    {
        return new MaskedImage(width, height, new byte[checked(width * height * 3)], new byte[checked(width * height)],
                               hasGlobalMask ? new byte[checked(width * height)] : null);
    }

    internal MaskedImage Clone()
    {
        return new MaskedImage(Width, Height, (byte[])Pixels.Clone(), (byte[])Mask.Clone(),
                               GlobalMask is null ? null : (byte[])GlobalMask.Clone(),
                               _gradientY is null ? null : (byte[])_gradientY.Clone(),
                               _gradientX is null ? null : (byte[])_gradientX.Clone(), _gradientsComputed);
    }

    internal bool IsMasked(int y, int x)
    {
        return Mask[(y * Width) + x] != 0;
    }

    internal bool IsGloballyMasked(int y, int x)
    {
        return GlobalMask is not null && GlobalMask[(y * Width) + x] != 0;
    }

    internal void SetMask(int y, int x, bool value)
    {
        Mask[(y * Width) + x] = value ? (byte)1 : (byte)0;
    }

    internal void ClearMask()
    {
        Array.Clear(Mask);
    }

    internal bool ContainsMask(int y, int x, int patchRadius)
    {
        for (var deltaY = -patchRadius; deltaY <= patchRadius; deltaY++)
        {
            for (var deltaX = -patchRadius; deltaX <= patchRadius; deltaX++)
            {
                var candidateY = y + deltaY;
                var candidateX = x + deltaX;
                if (candidateY >= 0                  && candidateY < Height && candidateX >= 0 && candidateX < Width &&
                    IsMasked(candidateY, candidateX) && !IsGloballyMasked(candidateY, candidateX))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal MaskedImage Downsample()
    {
        var newWidth  = Width  / 2;
        var newHeight = Height / 2;
        var result    = CreateEmpty(newWidth, newHeight, GlobalMask is not null);

        for (var y = 0; y < Height - 1; y += 2)
        {
            for (var x = 0; x < Width - 1; x += 2)
            {
                DownsamplePixel(result, y, x);
            }
        }

        return result;
    }

    private void DownsamplePixel(MaskedImage result, int centerY, int centerX)
    {
        var accumulator = new DownsampleAccumulator { GloballyMasked = true };
        for (var deltaY = -2; deltaY <= 3; deltaY++)
        {
            for (var deltaX = -2; deltaX <= 3; deltaX++)
            {
                AccumulateDownsamplePixel(ref accumulator, centerY, centerX, deltaY, deltaX);
            }
        }

        WriteDownsamplePixel(result, centerY / 2, centerX / 2, accumulator);
    }

    private void AccumulateDownsamplePixel(ref DownsampleAccumulator accumulator, int centerY, int centerX, int deltaY,
                                           int                       deltaX)
    {
        var sourceY = centerY + deltaY;
        var sourceX = centerX + deltaX;
        if (sourceY < 0 || sourceY >= Height || sourceX < 0 || sourceX >= Width)
        {
            return;
        }

        accumulator.GloballyMasked &= IsGloballyMasked(sourceY, sourceX);
        if (IsMasked(sourceY, sourceX))
        {
            return;
        }

        var kernel      = DownsampleKernel[2 + deltaY]  * DownsampleKernel[2 + deltaX];
        var pixelOffset = ((sourceY * Width) + sourceX) * 3;
        accumulator.Channel0  += Pixels[pixelOffset]     * kernel;
        accumulator.Channel1  += Pixels[pixelOffset + 1] * kernel;
        accumulator.Channel2  += Pixels[pixelOffset + 2] * kernel;
        accumulator.KernelSum += kernel;
    }

    private static void WriteDownsamplePixel(MaskedImage           result, int targetY, int targetX,
                                             DownsampleAccumulator accumulator)
    {
        var targetPixel = (targetY * result.Width) + targetX;
        if (result.GlobalMask is not null)
        {
            result.GlobalMask[targetPixel] = accumulator.GloballyMasked ? (byte)1 : (byte)0;
        }

        if (accumulator.KernelSum == 0)
        {
            result.Mask[targetPixel] = 1;
            return;
        }

        var targetOffset = targetPixel                                * 3;
        result.Pixels[targetOffset]     = (byte)(accumulator.Channel0 / accumulator.KernelSum);
        result.Pixels[targetOffset + 1] = (byte)(accumulator.Channel1 / accumulator.KernelSum);
        result.Pixels[targetOffset + 2] = (byte)(accumulator.Channel2 / accumulator.KernelSum);
        result.Mask[targetPixel]        = 0;
    }

    internal MaskedImage Upsample(int newWidth, int newHeight, byte[]? newGlobalMask = null)
    {
        var result = CreateEmpty(newWidth, newHeight, GlobalMask is not null);
        for (var y = 0; y < newHeight; y++)
        {
            for (var x = 0; x < newWidth; x++)
            {
                var sourceY     = y * Height / newHeight;
                var sourceX     = x * Width  / newWidth;
                var targetPixel = (y * newWidth) + x;

                if (IsGloballyMasked(sourceY, sourceX))
                {
                    result.GlobalMask![targetPixel] = 1;
                    result.Mask[targetPixel]        = 1;
                    continue;
                }

                if (result.GlobalMask is not null)
                {
                    result.GlobalMask[targetPixel] = 0;
                }

                if (IsMasked(sourceY, sourceX))
                {
                    result.Mask[targetPixel] = 1;
                    continue;
                }

                var sourceOffset = ((sourceY * Width) + sourceX) * 3;
                var targetOffset = targetPixel                   * 3;
                result.Pixels[targetOffset]     = Pixels[sourceOffset];
                result.Pixels[targetOffset + 1] = Pixels[sourceOffset + 1];
                result.Pixels[targetOffset + 2] = Pixels[sourceOffset + 2];
                result.Mask[targetPixel]        = 0;
            }
        }

        if (newGlobalMask is not null)
        {
            result.GlobalMask = newGlobalMask;
        }

        return result;
    }

    private void EnsureGradients()
    {
        if (_gradientsComputed)
        {
            return;
        }

        _gradientY = new byte[Pixels.Length];
        _gradientX = new byte[Pixels.Length];

        var timestamp = StageProfiler.Begin();

        for (var y = 1; y < Height - 1; y++)
        {
            var rowOffset         = y       * Width * 3;
            var previousRowOffset = (y - 1) * Width * 3;
            var nextRowOffset     = (y + 1) * Width * 3;
            for (var byteOffset = 3; byteOffset < Width * 3 - 3; byteOffset++)
            {
                _gradientY[rowOffset + byteOffset] = unchecked((byte)((Pixels[nextRowOffset + byteOffset] / 2) -
                    (Pixels[previousRowOffset                                               + byteOffset] / 2) + 128));

                _gradientX[rowOffset + byteOffset] = unchecked((byte)((Pixels[rowOffset + byteOffset + 3] / 2) -
                    (Pixels[rowOffset + byteOffset                                      - 3]              / 2) + 128));
            }
        }

        StageProfiler.End("gradients", timestamp);
        _gradientsComputed = true;
    }

    private struct DownsampleAccumulator
    {
        internal int  Channel0;
        internal int  Channel1;
        internal int  Channel2;
        internal int  KernelSum;
        internal bool GloballyMasked;
    }
}
