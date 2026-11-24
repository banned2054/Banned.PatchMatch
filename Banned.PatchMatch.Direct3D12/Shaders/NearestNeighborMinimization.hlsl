#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "SRV(t3), " \
    "SRV(t4), " \
    "SRV(t5), " \
    "UAV(u0), " \
    "RootConstants(num32BitConstants=10, b0)"

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
    int propagationDirection;
    uint checkerboardParity;
    uint conditionalSkip;
    uint sourceToTarget;
    uint sweepIndex;
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

void TryUpdate(int sourceY, int sourceX, int targetY, int targetX,
               inout uint bestTargetY, inout uint bestTargetX, inout uint bestDistance)
{
    if (targetY < 0 || targetY >= (int)imageHeight || targetX < 0 || targetX >= (int)imageWidth)
    {
        return;
    }

    uint targetPixel = (uint)(targetY * (int)imageWidth + targetX);
    if (LoadByte(targetGlobal, targetPixel) != 0)
    {
        return;
    }

    uint candidateDistance = CalculateRawDistance(sourceY, sourceX, targetY, targetX);
    if (candidateDistance < bestDistance)
    {
        bestTargetY = (uint)targetY;
        bestTargetX = (uint)targetX;
        bestDistance = candidateDistance;
    }
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

    uint sourceY = sourcePixel / imageWidth;
    uint sourceX = sourcePixel % imageWidth;
    if (((sourceY + sourceX) & 1u) != checkerboardParity || LoadByte(sourceGlobal, sourcePixel) != 0)
    {
        return;
    }

    if (conditionalSkip != 0)
    {
        bool sourceIsMasked = LoadByte(sourceInvalid, sourcePixel) != 0;
        if ((sourceToTarget != 0 && !sourceIsMasked) || (sourceToTarget == 0 && sourceIsMasked))
        {
            return;
        }
    }

    uint fieldOffset = sourcePixel * 12u;
    uint3 current = nearestNeighborField.Load3(fieldOffset);
    uint bestTargetY = current.x;
    uint bestTargetX = current.y;
    uint bestDistance = current.z;
    if (bestDistance == 0)
    {
        return;
    }

    int direction = propagationDirection;
    int neighborY = (int)sourceY - direction;
    if (neighborY >= 0 && neighborY < (int)imageHeight)
    {
        uint neighborPixel = (uint)neighborY * imageWidth + sourceX;
        if (LoadByte(sourceGlobal, neighborPixel) == 0)
        {
            uint3 neighbor = nearestNeighborField.Load3(neighborPixel * 12u);
            TryUpdate((int)sourceY, (int)sourceX, (int)neighbor.x + direction, (int)neighbor.y,
                      bestTargetY, bestTargetX, bestDistance);
        }
    }

    int neighborX = (int)sourceX - direction;
    if (neighborX >= 0 && neighborX < (int)imageWidth)
    {
        uint neighborPixel = sourceY * imageWidth + (uint)neighborX;
        if (LoadByte(sourceGlobal, neighborPixel) == 0)
        {
            uint3 neighbor = nearestNeighborField.Load3(neighborPixel * 12u);
            TryUpdate((int)sourceY, (int)sourceX, (int)neighbor.x, (int)neighbor.y + direction,
                      bestTargetY, bestTargetX, bestDistance);
        }
    }

    uint randomState = MixSeed(randomSeed ^ (sourcePixel * 0x9e3779b9u) ^ (sweepIndex * 0x85ebca6bu));
    int randomScale = ((int)min(imageHeight, imageWidth) - 1) / 2;
    while (randomScale > 0)
    {
        uint diameter = (uint)(2 * randomScale + 1);
        int targetY = (int)bestTargetY + (int)NextRandom(randomState, diameter) - randomScale;
        int targetX = (int)bestTargetX + (int)NextRandom(randomState, diameter) - randomScale;
        targetY = clamp(targetY, 0, (int)imageHeight - 1);
        targetX = clamp(targetX, 0, (int)imageWidth - 1);
        TryUpdate((int)sourceY, (int)sourceX, targetY, targetX,
                  bestTargetY, bestTargetX, bestDistance);
        randomScale /= 2;
    }

    nearestNeighborField.Store3(fieldOffset, uint3(bestTargetY, bestTargetX, bestDistance));
}
