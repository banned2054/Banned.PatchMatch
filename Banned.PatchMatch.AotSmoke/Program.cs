using Banned.PatchMatch;

const int width  = 7;
const int height = 7;

var image = new byte[width * height * 3];
for (var pixel = 0; pixel < width * height; pixel++)
{
    var offset = pixel * 3;
    image[offset]     = 20;
    image[offset + 1] = 40;
    image[offset + 2] = 60;
}

var mask         = new byte[width * height];
var centerPixel  = height / 2     * width + width / 2;
var centerOffset = centerPixel * 3;
image[centerOffset]     = byte.MaxValue;
image[centerOffset + 1] = byte.MaxValue;
image[centerOffset + 2] = byte.MaxValue;
mask[centerPixel]       = 1;

var result = PatchMatchInpainter.Inpaint(image, mask, width, height, new PatchMatchOptions { PatchRadius = 1 });

if (result[centerOffset] != 20 || result[centerOffset + 1] != 40 || result[centerOffset + 2] != 60)
{
    throw new InvalidOperationException("PatchMatch NativeAOT smoke test failed.");
}

Console.WriteLine("PatchMatch NativeAOT smoke test passed.");
