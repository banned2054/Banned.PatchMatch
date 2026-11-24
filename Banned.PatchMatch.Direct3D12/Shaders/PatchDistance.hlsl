#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "SRV(t3), " \
    "SRV(t4), " \
    "UAV(u0), " \
    "RootConstants(num32BitConstants=5, b0)"

ByteAddressBuffer sourcePixels : register(t0);
ByteAddressBuffer sourceInvalid : register(t1);
ByteAddressBuffer targetPixels : register(t2);
ByteAddressBuffer targetInvalid : register(t3);
ByteAddressBuffer patchPairs : register(t4);
RWByteAddressBuffer accumulators : register(u0);

cbuffer Constants : register(b0)
{
    uint pairCount;
    uint imageWidth;
    uint imageHeight;
    uint patchRadius;
    uint dispatchWidth;
};

uint LoadByte(ByteAddressBuffer buffer, uint byteOffset)
{
    uint word = buffer.Load(byteOffset & ~3u);
    return (word >> ((byteOffset & 3u) * 8)) & 0xffu;
}

uint GradientX(ByteAddressBuffer pixels, uint pixelOffset, uint channel)
{
    int next = (int)(LoadByte(pixels, pixelOffset + 3 + channel) / 2);
    int previous = (int)(LoadByte(pixels, pixelOffset - 3 + channel) / 2);
    return (uint)((next - previous + 128) & 0xff);
}

uint GradientY(ByteAddressBuffer pixels, uint pixelOffset, uint channel, uint rowStride)
{
    int next = (int)(LoadByte(pixels, pixelOffset + rowStride + channel) / 2);
    int previous = (int)(LoadByte(pixels, pixelOffset - rowStride + channel) / 2);
    return (uint)((next - previous + 128) & 0xff);
}

void AddSquareDifference(inout uint squaredSum, uint left, uint right)
{
    int difference = (int)left - (int)right;
    squaredSum += (uint)(difference * difference);
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint pairIndex = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    if (pairIndex >= pairCount)
    {
        return;
    }

    uint pairOffset = pairIndex * 16;
    int sourceY = asint(patchPairs.Load(pairOffset));
    int sourceX = asint(patchPairs.Load(pairOffset + 4));
    int targetY = asint(patchPairs.Load(pairOffset + 8));
    int targetX = asint(patchPairs.Load(pairOffset + 12));
    int radius = (int)patchRadius;
    uint rowStride = imageWidth * 3;
    uint squaredSum = 0;
    uint validPixels = 0;

    for (int deltaY = -radius; deltaY <= radius; deltaY++)
    {
        int sourcePatchY = sourceY + deltaY;
        int targetPatchY = targetY + deltaY;
        if (sourcePatchY <= 0 || sourcePatchY >= (int)imageHeight - 1 ||
            targetPatchY <= 0 || targetPatchY >= (int)imageHeight - 1)
        {
            continue;
        }

        for (int deltaX = -radius; deltaX <= radius; deltaX++)
        {
            int sourcePatchX = sourceX + deltaX;
            int targetPatchX = targetX + deltaX;
            if (sourcePatchX <= 0 || sourcePatchX >= (int)imageWidth - 1 ||
                targetPatchX <= 0 || targetPatchX >= (int)imageWidth - 1)
            {
                continue;
            }

            uint sourcePixel = (uint)(sourcePatchY * (int)imageWidth + sourcePatchX);
            uint targetPixel = (uint)(targetPatchY * (int)imageWidth + targetPatchX);
            if (LoadByte(sourceInvalid, sourcePixel) != 0 || LoadByte(targetInvalid, targetPixel) != 0)
            {
                continue;
            }

            uint sourceOffset = sourcePixel * 3;
            uint targetOffset = targetPixel * 3;
            for (uint channel = 0; channel < 3; channel++)
            {
                AddSquareDifference(squaredSum, LoadByte(sourcePixels, sourceOffset + channel),
                                   LoadByte(targetPixels, targetOffset + channel));
                AddSquareDifference(squaredSum, GradientX(sourcePixels, sourceOffset, channel),
                                   GradientX(targetPixels, targetOffset, channel));
                AddSquareDifference(squaredSum, GradientY(sourcePixels, sourceOffset, channel, rowStride),
                                   GradientY(targetPixels, targetOffset, channel, rowStride));
            }

            validPixels++;
        }
    }

    accumulators.Store2(pairIndex * 8, uint2(squaredSum, validPixels));
}
