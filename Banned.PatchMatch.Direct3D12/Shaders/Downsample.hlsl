#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "UAV(u0), " \
    "UAV(u1), " \
    "UAV(u2), " \
    "UAV(u3), " \
    "UAV(u4), " \
    "UAV(u5), " \
    "UAV(u6), " \
    "RootConstants(num32BitConstants=3, b0)"

ByteAddressBuffer sourcePixels : register(t0);
ByteAddressBuffer sourceMask : register(t1);
ByteAddressBuffer sourceGlobal : register(t2);
RWByteAddressBuffer downsampledSourcePixels : register(u0);
RWByteAddressBuffer downsampledSourceMask : register(u1);
RWByteAddressBuffer downsampledSourceGlobal : register(u2);
RWByteAddressBuffer downsampledSourceInvalid : register(u3);
RWByteAddressBuffer downsampledTargetPixels : register(u4);
RWByteAddressBuffer downsampledTargetInvalid : register(u5);
RWByteAddressBuffer downsampledTargetGlobal : register(u6);

cbuffer Constants : register(b0)
{
    uint sourceWidth;
    uint sourceHeight;
    uint dispatchWidth;
};

static const uint DownsampleKernel[6] = {1u, 5u, 10u, 10u, 5u, 1u};

uint LoadByte(ByteAddressBuffer buffer, uint byteOffset)
{
    uint word = buffer.Load(byteOffset & ~3u);
    return (word >> ((byteOffset & 3u) * 8)) & 0xffu;
}

struct DownsampledPixel
{
    uint3 color;
    uint hole;
    uint globallyMasked;
};

DownsampledPixel DownsamplePixel(uint coarsePixel)
{
    uint width = sourceWidth / 2u;
    int centerY = (int)((coarsePixel / width) * 2u);
    int centerX = (int)((coarsePixel % width) * 2u);
    bool globallyMasked = true;
    uint channel0 = 0u;
    uint channel1 = 0u;
    uint channel2 = 0u;
    uint kernelSum = 0u;
    for (int deltaY = -2; deltaY <= 3; deltaY++)
    {
        int sourceY = centerY + deltaY;
        if (sourceY < 0 || sourceY >= (int)sourceHeight)
        {
            continue;
        }

        for (int deltaX = -2; deltaX <= 3; deltaX++)
        {
            int sourceX = centerX + deltaX;
            if (sourceX < 0 || sourceX >= (int)sourceWidth)
            {
                continue;
            }

            uint sample = (uint)(sourceY * (int)sourceWidth + sourceX);
            globallyMasked = globallyMasked && (LoadByte(sourceGlobal, sample) != 0u);
            if (LoadByte(sourceMask, sample) != 0u)
            {
                continue;
            }

            uint kernel = DownsampleKernel[deltaY + 2] * DownsampleKernel[deltaX + 2];
            uint offset = sample * 3u;
            channel0 += LoadByte(sourcePixels, offset) * kernel;
            channel1 += LoadByte(sourcePixels, offset + 1u) * kernel;
            channel2 += LoadByte(sourcePixels, offset + 2u) * kernel;
            kernelSum += kernel;
        }
    }

    DownsampledPixel result;
    if (kernelSum == 0u)
    {
        result.color = uint3(0u, 0u, 0u);
        result.hole = 1u;
    }
    else
    {
        result.color = uint3(channel0 / kernelSum, channel1 / kernelSum, channel2 / kernelSum);
        result.hole = 0u;
    }

    result.globallyMasked = globallyMasked ? 1u : 0u;
    return result;
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint thread = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    uint pixelCount = (sourceWidth / 2u) * (sourceHeight / 2u);
    uint imageWordCount = (pixelCount * 3u + 3u) / 4u;
    uint maskWordCount = (pixelCount + 3u) / 4u;
    if (thread >= imageWordCount + maskWordCount)
    {
        return;
    }

    // Image threads pack one 4-byte word of the tightly packed RGB output, which
    // spans at most two pixels. Mask threads pack one 4-byte word of every mask.
    if (thread < imageWordCount)
    {
        uint byteOffset = thread * 4u;
        uint firstPixel = byteOffset / 3u;
        uint secondPixel = firstPixel + 1u;
        uint3 firstColor = DownsamplePixel(firstPixel).color;
        uint3 secondColor = uint3(0u, 0u, 0u);
        if (secondPixel < pixelCount)
        {
            secondColor = DownsamplePixel(secondPixel).color;
        }

        uint packedWord = 0u;
        for (uint byteIndex = 0u; byteIndex < 4u; byteIndex++)
        {
            uint imageByte = byteOffset + byteIndex;
            uint pixel = imageByte / 3u;
            if (pixel >= pixelCount)
            {
                break;
            }

            uint channel = imageByte - pixel * 3u;
            uint3 color = pixel == firstPixel ? firstColor : secondColor;
            packedWord |= (channel == 0u ? color.x : channel == 1u ? color.y : color.z) << (byteIndex * 8u);
        }

        downsampledSourcePixels.Store(byteOffset, packedWord);
        downsampledTargetPixels.Store(byteOffset, packedWord);
        return;
    }

    uint maskOffset = (thread - imageWordCount) * 4u;
    uint holeWord = 0u;
    uint globalWord = 0u;
    for (uint byteIndex = 0u; byteIndex < 4u; byteIndex++)
    {
        uint pixel = maskOffset + byteIndex;
        if (pixel >= pixelCount)
        {
            break;
        }

        DownsampledPixel downsampled = DownsamplePixel(pixel);
        holeWord |= downsampled.hole << (byteIndex * 8u);
        globalWord |= downsampled.globallyMasked << (byteIndex * 8u);
    }

    downsampledSourceMask.Store(maskOffset, holeWord);
    downsampledSourceGlobal.Store(maskOffset, globalWord);
    downsampledSourceInvalid.Store(maskOffset, holeWord | globalWord);
    // The new target has no holes, so its merged invalid mask (hole | global) is exactly its global mask.
    downsampledTargetInvalid.Store(maskOffset, globalWord);
    downsampledTargetGlobal.Store(maskOffset, globalWord);
}
