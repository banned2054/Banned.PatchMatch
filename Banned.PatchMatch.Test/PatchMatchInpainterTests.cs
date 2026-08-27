namespace Banned.PatchMatch.Test;

public class PatchMatchInpainterTests
{
    private static readonly PatchMatchOptions FastOptions = new()
    {
        PatchRadius = 1,
        RandomSeed  = 1212
    };

    [Test]
    public void Inpaint_WithoutMaskedPixels_PreservesImage()
    {
        const int width    = 9;
        const int height   = 7;
        var       image    = CreatePattern(width, height);
        var       original = (byte[])image.Clone();
        var       mask     = new byte[width * height];

        var result = PatchMatchInpainter.Inpaint(image, mask, width, height, FastOptions);

        Assert.That(result, Is.EqualTo(original));
        Assert.That(image, Is.EqualTo(original));
    }

    [Test]
    public void Inpaint_SolidColorHole_RestoresKnownColor()
    {
        const int width        = 9;
        const int height       = 9;
        var       image        = CreateSolidImage(width, height, 17, 83, 149);
        var       mask         = new byte[width * height];
        var       centerPixel  = ((height / 2) * width) + (width / 2);
        var       centerOffset = centerPixel * 3;
        image[centerOffset]     = byte.MaxValue;
        image[centerOffset + 1] = byte.MaxValue;
        image[centerOffset + 2] = byte.MaxValue;
        mask[centerPixel]       = byte.MaxValue;

        var result = PatchMatchInpainter.Inpaint(image, mask, width, height, FastOptions);

        Assert.That(result[centerOffset], Is.EqualTo(17));
        Assert.That(result[centerOffset + 1], Is.EqualTo(83));
        Assert.That(result[centerOffset + 2], Is.EqualTo(149));
    }

    [Test]
    public void Inpaint_WithSameSeed_IsDeterministic()
    {
        const int width  = 11;
        const int height = 9;
        var       image  = CreatePattern(width, height);
        var       mask   = new byte[width * height];
        for (var y = 3; y <= 5; y++)
        {
            for (var x = 4; x <= 6; x++)
            {
                mask[(y * width) + x] = 1;
            }
        }

        var first  = PatchMatchInpainter.Inpaint(image, mask, width, height, FastOptions);
        var second = PatchMatchInpainter.Inpaint(image, mask, width, height, FastOptions);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Inpaint_GlobalMask_DoesNotLeakExcludedColorIntoValidPixels()
    {
        const int width        = 7;
        const int height       = 7;
        var       image        = CreateSolidImage(width, height, 10, 20, 30);
        var       mask         = new byte[width * height];
        var       globalMask   = new byte[width * height];
        var       centerPixel  = ((height / 2) * width) + (width / 2);
        var       centerOffset = centerPixel * 3;
        image[centerOffset]     = 201;
        image[centerOffset + 1] = 202;
        image[centerOffset + 2] = 203;
        mask[centerPixel]       = 1;
        globalMask[centerPixel] = 1;

        var result = PatchMatchInpainter.Inpaint(image, mask, globalMask, width, height, FastOptions);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            if (pixel == centerPixel)
            {
                continue;
            }

            var offset = pixel * 3;
            Assert.That(result[offset], Is.EqualTo(10));
            Assert.That(result[offset + 1], Is.EqualTo(20));
            Assert.That(result[offset + 2], Is.EqualTo(30));
        }
    }

    [Test]
    public void Inpaint_WithoutExplicitMask_TreatsPureWhiteAsHole()
    {
        const int width        = 7;
        const int height       = 7;
        var       image        = CreateSolidImage(width, height, 30, 60, 90);
        var       centerPixel  = ((height / 2) * width) + (width / 2);
        var       centerOffset = centerPixel * 3;
        image[centerOffset]     = byte.MaxValue;
        image[centerOffset + 1] = byte.MaxValue;
        image[centerOffset + 2] = byte.MaxValue;

        var result = PatchMatchInpainter.Inpaint(image, width, height, FastOptions);

        Assert.That(result[centerOffset], Is.EqualTo(30));
        Assert.That(result[centerOffset + 1], Is.EqualTo(60));
        Assert.That(result[centerOffset + 2], Is.EqualTo(90));
    }

    [TestCase(2)]
    [TestCase(3)]
    public void InpaintRegularity_AcceptsTwoAndThreeChannelGuideMaps(int channels)
    {
        const int width  = 7;
        const int height = 7;
        var       image  = CreatePattern(width, height);
        var       mask   = new byte[width * height];
        mask[((height / 2) * width) + (width / 2)] = 1;
        var guideMap = CreateGuideMap(width, height, channels);

        var first  = PatchMatchInpainter.InpaintRegularity(image, mask, guideMap, width, height, options : FastOptions);
        var second = PatchMatchInpainter.InpaintRegularity(image, mask, guideMap, width, height, options : FastOptions);

        Assert.That(first, Has.Length.EqualTo(image.Length));
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Inpaint_DestinationOverload_WritesResult()
    {
        const int width       = 5;
        const int height      = 5;
        var       image       = CreatePattern(width, height);
        var       mask        = new byte[width * height];
        var       destination = new byte[image.Length];

        PatchMatchInpainter.Inpaint(image, mask, width, height, destination, FastOptions);

        Assert.That(destination, Is.EqualTo(image));
    }

    [Test]
    public void Inpaint_CanceledToken_ThrowsBeforeProcessing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var image = CreateSolidImage(3, 3, 1, 2, 3);
        var mask  = new byte[9];

        Assert.That(() => PatchMatchInpainter.Inpaint(image, mask, 3, 3, FastOptions, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(3)]
    public void Inpaint_InvalidPatchRadius_Throws(int patchRadius)
    {
        var image   = CreateSolidImage(3, 3, 1, 2, 3);
        var mask    = new byte[9];
        var options = new PatchMatchOptions { PatchRadius = patchRadius };

        Assert.That(
                    () => PatchMatchInpainter.Inpaint(image, mask, 3, 3, options),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Inpaint_InvalidBufferLengths_ThrowMeaningfulErrors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => PatchMatchInpainter.Inpaint(new byte[8], new byte[9], 3, 3, FastOptions),
                        Throws.TypeOf<ArgumentException>());
            Assert.That(() => PatchMatchInpainter.Inpaint(new byte[27], new byte[8], 3, 3, FastOptions),
                        Throws.TypeOf<ArgumentException>());
            Assert.That(() => PatchMatchInpainter.InpaintRegularity(new byte[27], new byte[9], new float[9], 3, 3,
                                                                    options : FastOptions),
                        Throws.TypeOf<ArgumentException>());
        });
    }

    private static byte[] CreateSolidImage(int width, int height, byte channel0, byte channel1, byte channel2)
    {
        var result = new byte[width * height * 3];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 3;
            result[offset]     = channel0;
            result[offset + 1] = channel1;
            result[offset + 2] = channel2;
        }

        return result;
    }

    private static byte[] CreatePattern(int width, int height)
    {
        var result = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width)           + x)      * 3;
                result[offset]     = (byte)((x * 17 + y * 3)  % 256);
                result[offset + 1] = (byte)((x * 5  + y * 19) % 256);
                result[offset + 2] = (byte)((x * 11 + y * 13) % 256);
            }
        }

        return result;
    }

    private static float[] CreateGuideMap(int width, int height, int channels)
    {
        var result = new float[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * channels;
                result[offset]     = (float)x / width;
                result[offset + 1] = (float)y / height;
            }
        }

        return result;
    }
}
