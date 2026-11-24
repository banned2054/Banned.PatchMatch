#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "SRV(t3), " \
    "SRV(t4), " \
    "SRV(t5), " \
    "UAV(u0), " \
    "RootConstants(num32BitConstants=6, b0)"

ByteAddressBuffer sourcePixels : register(t0);
ByteAddressBuffer sourceInvalid : register(t1);
ByteAddressBuffer sourceGlobal : register(t2);
ByteAddressBuffer targetPixels : register(t3);
ByteAddressBuffer targetInvalid : register(t4);
ByteAddressBuffer targetGlobal : register(t5);
RWByteAddressBuffer nearestNeighborField : register(u0);

cbuffer Constants : register(b0)
{
    uint imageWidth;
    uint imageHeight;
    uint patchRadius;
    uint randomSeed;
    uint dispatchWidth;
    uint maximumAttempts;
};

static const uint SsdScale = 9u * 255u * 255u;

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

uint MixSeed(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

uint NextRandom(inout uint state, uint maxExclusive)
{
    state = state * 214013u + 2531011u;
    return ((state >> 16) & 0x7fffu) % maxExclusive;
}

uint CalculateRawDistance(int sourceY, int sourceX, int targetY, int targetX)
{
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

    uint patchWidth = patchRadius * 2u + 1u;
    uint patchPixels = patchWidth * patchWidth;
    return SsdScale * (patchPixels - validPixels) + squaredSum;
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint sourcePixel = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    uint pixelCount = imageWidth * imageHeight;
    if (sourcePixel >= pixelCount)
    {
        return;
    }

    uint fieldOffset = sourcePixel * 12u;
    if (LoadByte(sourceGlobal, sourcePixel) != 0)
    {
        nearestNeighborField.Store3(fieldOffset, uint3(0, 0, 0));
        return;
    }

    int sourceY = (int)(sourcePixel / imageWidth);
    int sourceX = (int)(sourcePixel % imageWidth);
    uint patchWidth = patchRadius * 2u + 1u;
    uint maximumDistance = SsdScale * patchWidth * patchWidth;
    uint bestDistance = maximumDistance;
    uint targetY = 0;
    uint targetX = 0;
    uint randomState = MixSeed(randomSeed ^ (sourcePixel * 0x9e3779b9u));

    for (uint attempt = 0; attempt < maximumAttempts; attempt++)
    {
        targetY = NextRandom(randomState, imageHeight);
        targetX = NextRandom(randomState, imageWidth);
        uint targetPixel = targetY * imageWidth + targetX;
        if (LoadByte(targetGlobal, targetPixel) != 0)
        {
            continue;
        }

        uint candidateDistance = CalculateRawDistance(sourceY, sourceX, (int)targetY, (int)targetX);
        bestDistance = candidateDistance;
        if (candidateDistance < maximumDistance)
        {
            break;
        }
    }

    nearestNeighborField.Store3(fieldOffset, uint3(targetY, targetX, bestDistance));
}
