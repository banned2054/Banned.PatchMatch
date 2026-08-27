using Banned.PatchMatch.Internal;

namespace Banned.PatchMatch;

/// <summary>
/// 提供基于 PatchMatch 的纯托管图像修复功能。<br/>
/// Provides pure managed PatchMatch-based image inpainting.
/// </summary>
/// <remarks>
/// 输入图像必须是连续排列的三通道字节缓冲区。算法保持通道顺序，因此同时支持 RGB 和 BGR。
/// 蒙版每个像素占一个字节；零表示保留，任意非零值表示缺失。<br/>
/// Input images must be tightly packed three-channel byte buffers. Channel order is preserved, so
/// both RGB and BGR data are supported. Masks contain one byte per pixel; zero keeps a pixel and any
/// non-zero value marks it as missing.
/// </remarks>
public static class PatchMatchInpainter
{
    /// <summary>
    /// 使用指定的孔洞蒙版修复图像。<br/>
    /// Inpaints an image using a supplied hole mask.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版，任意非零值表示需要修复。 / Single-channel hole mask; any non-zero value marks a hole.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    /// <returns>修复后的连续三通道图像字节。 / Inpainted tightly packed three-channel image bytes.</returns>
    public static byte[] Inpaint(ReadOnlySpan<byte> image,          ReadOnlySpan<byte> mask, int width, int height,
                                 PatchMatchOptions? options = null, CancellationToken  cancellationToken = default)
    {
        return InpaintCore(image, mask, default, default, 0, width, height, options, cancellationToken);
    }

    /// <summary>
    /// 修复图像，并从匹配和投票中排除全局蒙版像素。<br/>
    /// Inpaints an image and excludes globally masked pixels from matching and voting.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="globalMask">从匹配和投票中排除像素的全局蒙版。 / Global mask that excludes pixels from matching and voting.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    /// <returns>修复后的连续三通道图像字节。 / Inpainted tightly packed three-channel image bytes.</returns>
    public static byte[] Inpaint(ReadOnlySpan<byte> image, ReadOnlySpan<byte> mask,   ReadOnlySpan<byte> globalMask,
                                 int                width, int                height, PatchMatchOptions? options = null,
                                 CancellationToken  cancellationToken = default)
    {
        if (globalMask.IsEmpty)
        {
            throw new ArgumentException("The global mask cannot be empty for this overload.", nameof(globalMask));
        }

        return InpaintCore(image, mask, globalMask, default, 0, width, height, options, cancellationToken);
    }

    /// <summary>
    /// 在没有显式孔洞蒙版时，将纯白像素识别为孔洞并进行修复。<br/>
    /// Inpaints pixels that are pure white when no explicit hole mask is available.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    /// <returns>修复后的连续三通道图像字节。 / Inpainted tightly packed three-channel image bytes.</returns>
    public static byte[] Inpaint(ReadOnlySpan<byte> image, int width, int height, PatchMatchOptions? options = null,
                                 CancellationToken  cancellationToken = default)
    {
        var pixelCount = ValidateImage(image, width, height);
        var mask       = new byte[pixelCount];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 3;
            if (image[offset]     == byte.MaxValue &&
                image[offset + 1] == byte.MaxValue &&
                image[offset + 2] == byte.MaxValue)
            {
                mask[pixel] = 1;
            }
        }

        return Inpaint(image, mask, width, height, options, cancellationToken);
    }

    /// <summary>
    /// 修复图像并写入调用方提供的目标缓冲区。<br/>
    /// Inpaints an image into a caller-provided destination buffer.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="destination">接收修复结果的目标缓冲区。 / Destination buffer that receives the inpainted image.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    public static void Inpaint(ReadOnlySpan<byte> image,       ReadOnlySpan<byte> mask, int width, int height,
                               Span<byte>         destination, PatchMatchOptions? options = null,
                               CancellationToken  cancellationToken = default)
    {
        ValidateDestination(destination, width, height);
        Inpaint(image, mask, width, height, options, cancellationToken).CopyTo(destination);
    }

    /// <summary>
    /// 使用全局蒙版修复图像，并写入调用方提供的目标缓冲区。<br/>
    /// Inpaints an image into a caller-provided destination buffer while honoring a global mask.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="globalMask">从匹配和投票中排除像素的全局蒙版。 / Global mask that excludes pixels from matching and voting.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="destination">接收修复结果的目标缓冲区。 / Destination buffer that receives the inpainted image.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    public static void Inpaint(ReadOnlySpan<byte> image, ReadOnlySpan<byte> mask, ReadOnlySpan<byte> globalMask,
                               int width, int height, Span<byte> destination, PatchMatchOptions? options = null,
                               CancellationToken cancellationToken = default)
    {
        ValidateDestination(destination, width, height);
        Inpaint(image, mask, globalMask, width, height, options, cancellationToken).CopyTo(destination);
    }

    /// <summary>
    /// 使用周期双坐标引导图修复图像，以优先选择规律对应关系。<br/>
    /// Inpaints an image using a periodic two-coordinate guide map to prefer regular correspondences.
    /// </summary>
    /// <remarks>
    /// 引导图每个像素必须包含两个或三个 <see cref="float"/> 值。前两个通道保存归一化周期坐标；
    /// 为兼容 PyPatchMatch 可以传入第三通道，但算法会忽略它。<br/>
    /// The guide map must contain two or three <see cref="float"/> values per pixel. The first two
    /// channels contain normalized periodic coordinates; a third channel is accepted for compatibility
    /// with PyPatchMatch and ignored.
    /// </remarks>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="guideMap">每个像素包含两个或三个值的周期坐标引导图。 / Periodic coordinate guide map with two or three values per pixel.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="guideWeight">引导距离相对于图像距离的权重。 / Weight of the guide distance relative to image distance.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    /// <returns>修复后的连续三通道图像字节。 / Inpainted tightly packed three-channel image bytes.</returns>
    public static byte[] InpaintRegularity(ReadOnlySpan<byte>  image, ReadOnlySpan<byte> mask,
                                           ReadOnlySpan<float> guideMap, int width, int height,
                                           float               guideWeight = 0.25f, PatchMatchOptions? options = null,
                                           CancellationToken   cancellationToken = default)
    {
        return InpaintCore(image, mask, default, guideMap, guideWeight, width, height, options, cancellationToken);
    }

    /// <summary>
    /// 使用规律性引导图修复图像，并写入调用方提供的目标缓冲区。<br/>
    /// Inpaints an image with a regularity guide into a caller-provided destination buffer.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="guideMap">周期坐标引导图。 / Periodic coordinate guide map.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="destination">接收修复结果的目标缓冲区。 / Destination buffer that receives the inpainted image.</param>
    /// <param name="guideWeight">引导距离相对于图像距离的权重。 / Weight of the guide distance relative to image distance.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    public static void InpaintRegularity(ReadOnlySpan<byte>  image, ReadOnlySpan<byte> mask,
                                         ReadOnlySpan<float> guideMap, int width, int height, Span<byte> destination,
                                         float               guideWeight = 0.25f, PatchMatchOptions? options = null,
                                         CancellationToken   cancellationToken = default)
    {
        ValidateDestination(destination, width, height);
        InpaintRegularity(image, mask, guideMap, width, height, guideWeight, options, cancellationToken)
           .CopyTo(destination);
    }

    /// <summary>
    /// 同时使用周期引导图和全局排除蒙版修复图像。<br/>
    /// Inpaints an image using both a periodic guide map and a global exclusion mask.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="globalMask">从匹配和投票中排除像素的全局蒙版。 / Global mask that excludes pixels from matching and voting.</param>
    /// <param name="guideMap">周期坐标引导图。 / Periodic coordinate guide map.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="guideWeight">引导距离相对于图像距离的权重。 / Weight of the guide distance relative to image distance.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    /// <returns>修复后的连续三通道图像字节。 / Inpainted tightly packed three-channel image bytes.</returns>
    public static byte[] InpaintRegularity(ReadOnlySpan<byte> image, ReadOnlySpan<byte> mask,
                                           ReadOnlySpan<byte> globalMask, ReadOnlySpan<float> guideMap, int width,
                                           int height, float guideWeight = 0.25f, PatchMatchOptions? options = null,
                                           CancellationToken cancellationToken = default)
    {
        if (globalMask.IsEmpty)
        {
            throw new ArgumentException("The global mask cannot be empty for this overload.", nameof(globalMask));
        }

        return InpaintCore(image, mask, globalMask, guideMap, guideWeight, width, height, options, cancellationToken);
    }

    /// <summary>
    /// 使用规律性引导图和全局蒙版修复图像，并写入目标缓冲区。<br/>
    /// Inpaints an image with a regularity guide and global mask into a destination buffer.
    /// </summary>
    /// <param name="image">连续排列的三通道图像字节。 / Tightly packed three-channel image bytes.</param>
    /// <param name="mask">单通道孔洞蒙版。 / Single-channel hole mask.</param>
    /// <param name="globalMask">从匹配和投票中排除像素的全局蒙版。 / Global mask that excludes pixels from matching and voting.</param>
    /// <param name="guideMap">周期坐标引导图。 / Periodic coordinate guide map.</param>
    /// <param name="width">图像宽度。 / Image width.</param>
    /// <param name="height">图像高度。 / Image height.</param>
    /// <param name="destination">接收修复结果的目标缓冲区。 / Destination buffer that receives the inpainted image.</param>
    /// <param name="guideWeight">引导距离相对于图像距离的权重。 / Weight of the guide distance relative to image distance.</param>
    /// <param name="options">PatchMatch 配置；为 null 时使用默认配置。 / PatchMatch options; uses defaults when null.</param>
    /// <param name="cancellationToken">用于取消计算的令牌。 / Token used to cancel the operation.</param>
    public static void InpaintRegularity(ReadOnlySpan<byte> image, ReadOnlySpan<byte> mask,
                                         ReadOnlySpan<byte> globalMask, ReadOnlySpan<float> guideMap, int width,
                                         int                height, Span<byte> destination, float guideWeight = 0.25f,
                                         PatchMatchOptions? options           = null,
                                         CancellationToken  cancellationToken = default)
    {
        ValidateDestination(destination, width, height);
        InpaintRegularity(image, mask, globalMask, guideMap, width, height, guideWeight, options, cancellationToken)
           .CopyTo(destination);
    }

    private static byte[] InpaintCore(ReadOnlySpan<byte>  image, ReadOnlySpan<byte> mask, ReadOnlySpan<byte> globalMask,
                                      ReadOnlySpan<float> guideMap, float guideWeight, int width, int height,
                                      PatchMatchOptions?  options, CancellationToken cancellationToken)
    {
        var pixelCount = ValidateImage(image, width, height);
        ValidateMask(mask, pixelCount, nameof(mask));
        if (!globalMask.IsEmpty)
        {
            ValidateMask(globalMask, pixelCount, nameof(globalMask));
        }

        options ??= PatchMatchOptions.Default;
        options.Validate(width, height);
        cancellationToken.ThrowIfCancellationRequested();

        var                  initial = MaskedImage.Create(image, mask, globalMask, width, height);
        IPatchDistanceMetric metric;
        if (guideMap.IsEmpty)
        {
            metric = new PatchSsdDistanceMetric(options.PatchRadius);
        }
        else
        {
            var guideChannels = ValidateGuideMap(guideMap, pixelCount, guideWeight);
            metric = new RegularityGuidedDistanceMetric(options.PatchRadius, guideMap.ToArray(), width, height,
                                                        guideChannels, guideWeight);
        }

        var engine = new InpaintingEngine(initial, metric, options.RandomSeed, cancellationToken);
        return engine.Run();
    }

    private static int ValidateImage(ReadOnlySpan<byte> image, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        int pixelCount;
        int expectedLength;
        try
        {
            pixelCount     = checked(width      * height);
            expectedLength = checked(pixelCount * 3);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The supplied dimensions are too large.");
        }

        if (image.Length != expectedLength)
        {
            throw new
                ArgumentException($"The image must contain exactly {expectedLength} bytes for a {width}x{height} three-channel image.",
                                  nameof(image));
        }

        return pixelCount;
    }

    private static void ValidateMask(ReadOnlySpan<byte> mask, int pixelCount, string parameterName)
    {
        if (mask.Length != pixelCount)
        {
            throw new ArgumentException($"The mask must contain exactly {pixelCount} bytes.", parameterName);
        }
    }

    private static int ValidateGuideMap(ReadOnlySpan<float> guideMap, int pixelCount, float guideWeight)
    {
        if (!float.IsFinite(guideWeight) || guideWeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(guideWeight), guideWeight,
                                                  "The guide weight must be finite and non-negative.");
        }

        var twoChannelLength   = checked(pixelCount * 2);
        var threeChannelLength = checked(pixelCount * 3);
        var channels = guideMap.Length switch
        {
            var length when length == twoChannelLength   => 2,
            var length when length == threeChannelLength => 3,
            _ => throw new
                ArgumentException($"The guide map must contain either {twoChannelLength} or {threeChannelLength} values.",
                                  nameof(guideMap))
        };

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * channels;
            if (!float.IsFinite(guideMap[offset]) || !float.IsFinite(guideMap[offset + 1]))
            {
                throw new ArgumentException("The first two guide-map channels must be finite.", nameof(guideMap));
            }
        }

        return channels;
    }

    private static void ValidateDestination(Span<byte> destination, int width, int height)
    {
        int expectedLength;
        try
        {
            expectedLength = checked(width * height * 3);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The supplied dimensions are too large.");
        }

        if (destination.Length != expectedLength)
        {
            throw new ArgumentException($"The destination must contain exactly {expectedLength} bytes.",
                                        nameof(destination));
        }
    }
}
