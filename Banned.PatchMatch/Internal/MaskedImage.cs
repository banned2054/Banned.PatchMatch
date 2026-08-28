namespace Banned.PatchMatch.Internal;

internal sealed class MaskedImage
{
    private static readonly int[] DownsampleKernel = [1, 5, 10, 10, 5, 1];

    internal const int FeatureStride = 12;

    private byte[]?  _gradientY;
    private byte[]?  _gradientX;
    private byte[]?  _features;
    private ulong[]? _invalidBits;
    private bool     _gradientsComputed;

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

    /// <summary>
    /// 合并并压缩后的无效位图（1 = 不可用）。每个 ulong 表示 64 个像素，
    /// 距离内核可直接提取一段有效位，无需每行重复加载字节 mask 并执行向量比较。
    /// </summary>
    internal ulong[] InvalidBits
    {
        get
        {
            if (_invalidBits is not null)
            {
                return _invalidBits;
            }

            var invalidBits = new ulong[(Mask.Length + 63) / 64];
            for (var i = 0; i < Mask.Length; i++)
            {
                if (Mask[i] != 0 || (GlobalMask is not null && GlobalMask[i] != 0))
                {
                    invalidBits[i >> 6] |= 1ul << (i & 63);
                }
            }

            _invalidBits = invalidBits;
            return invalidBits;
        }
    }

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

    /// <summary>
    /// 每像素 12 字节的交织特征缓冲（3 像素 + 3 横向梯度 + 3 纵向梯度 + 3 字节零填充），
    /// 供 SIMD 距离内核将一个有效行程当作单一连续区间处理。相比 16 字节布局工作集减少 25%，
    /// 降低小图的缓存地址敏感性；两侧填充字节相同，对 SSD 的贡献恒为 0。
    /// 按需构建；像素被原地修改后必须失效。<br/>
    /// Interleaved 12-byte-per-pixel feature buffer (3 pixel + 3 X-gradient + 3 Y-gradient
    /// bytes + 3 zero-padding bytes) letting the SIMD distance kernel treat a valid run as one
    /// contiguous span. Its working set is 25% smaller than the 16-byte layout, reducing cache
    /// address sensitivity on small images; identical padding contributes exactly 0 to the SSD.
    /// Built on demand; must be invalidated after in-place mutation.
    /// </summary>
    internal byte[] Features
    {
        get
        {
            EnsureFeatures();
            return _features!;
        }
    }

    /// <summary>
    /// 失效特征缓冲。像素发生原地修改后调用，保证特征与像素一致。<br/>
    /// Invalidates the feature buffer. Call after in-place pixel mutation.
    /// </summary>
    internal void InvalidateFeatures()
    {
        _features = null;
    }

    private void EnsureFeatures()
    {
        if (_features is not null)
        {
            return;
        }

        var gradientX = GradientX;
        var gradientY = GradientY;
        var timestamp = StageProfiler.Begin();

        var features   = new byte[checked(Width * Height * FeatureStride)];
        var pixelCount = Width * Height;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 3;
            var target = pixel * FeatureStride;
            features[target]     = Pixels[offset];
            features[target + 1] = Pixels[offset + 1];
            features[target + 2] = Pixels[offset + 2];
            features[target + 3] = gradientX[offset];
            features[target + 4] = gradientX[offset + 1];
            features[target + 5] = gradientX[offset + 2];
            features[target + 6] = gradientY[offset];
            features[target + 7] = gradientY[offset + 1];
            features[target + 8] = gradientY[offset + 2];
        }

        StageProfiler.End("features", timestamp);
        _features = features;
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
        Mask[y * Width + x] = value ? (byte)1 : (byte)0;
        _invalidBits        = null;
    }

    internal void ClearMask()
    {
        Array.Clear(Mask);
        _invalidBits = null;
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
