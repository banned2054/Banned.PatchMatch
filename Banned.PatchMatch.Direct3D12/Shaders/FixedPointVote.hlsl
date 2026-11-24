#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "SRV(t3), " \
    "SRV(t4), " \
    "SRV(t5), " \
    "UAV(u0), " \
    "UAV(u1), " \
    "RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer originalPixels : register(t0);
ByteAddressBuffer originalInvalid : register(t1);
ByteAddressBuffer fieldSourceInvalid : register(t2);
ByteAddressBuffer fieldSourceGlobal : register(t3);
ByteAddressBuffer fieldTargetGlobal : register(t4);
ByteAddressBuffer similarityWeights : register(t5);
RWByteAddressBuffer nearestNeighborField : register(u0);
RWByteAddressBuffer votes : register(u1);

cbuffer Constants : register(b0)
{
    uint imageWidth;
    uint imageHeight;
    uint patchRadius;
    uint dispatchWidth;
    uint sourceToTarget;
    uint voteWidth;
    uint voteHeight;
    uint voteScale;
};

static const uint DistanceScale = 65535u;
static const uint SsdScale = 9u * 255u * 255u;

uint LoadByte(ByteAddressBuffer buffer, uint byteOffset)
{
    uint word = buffer.Load(byteOffset & ~3u);
    return (word >> ((byteOffset & 3u) * 8)) & 0xffu;
}

bool LessThanOrEqual64(uint leftLow, uint leftHigh, uint rightLow, uint rightHigh)
{
    return leftHigh < rightHigh || (leftHigh == rightHigh && leftLow <= rightLow);
}

void Multiply32To64(uint left, uint right, out uint low, out uint high)
{
    uint leftLow = left & 0xffffu;
    uint rightLow = right & 0xffffu;
    uint product = leftLow * rightLow;
    uint lowLow = product & 0xffffu;
    uint carry = product >> 16;

    uint leftHigh = left >> 16;
    product = leftHigh * rightLow + carry;
    uint middleLow = product & 0xffffu;
    uint middleHigh = product >> 16;

    uint rightHigh = right >> 16;
    product = leftLow * rightHigh + middleLow;
    carry = product >> 16;
    high = leftHigh * rightHigh + middleHigh + carry;
    low = (product << 16) + lowLow;
}

uint NormalizeRawDistance(uint rawDistance, uint denominator)
{
    if (rawDistance >= denominator)
    {
        return DistanceScale;
    }

    uint numeratorLow;
    uint numeratorHigh;
    Multiply32To64(rawDistance, DistanceScale, numeratorLow, numeratorHigh);

    uint quotient = 0;
    for (int bit = 15; bit >= 0; bit--)
    {
        uint candidate = quotient | (1u << bit);
        uint candidateLow;
        uint candidateHigh;
        Multiply32To64(candidate, denominator, candidateLow, candidateHigh);
        if (LessThanOrEqual64(candidateLow, candidateHigh, numeratorLow, numeratorHigh))
        {
            quotient = candidate;
        }
    }

    return quotient;
}

void AtomicAdd64(uint byteOffset, uint value)
{
    uint previousLow;
    votes.InterlockedAdd(byteOffset, value, previousLow);
    if (previousLow > 0xffffffffu - value)
    {
        uint ignored;
        votes.InterlockedAdd(byteOffset + 4u, 1u, ignored);
    }
}

void AddWeightedPixel(uint destinationPixel, uint sourcePixel, uint weight)
{
    uint sourceOffset = sourcePixel * 3u;
    uint voteOffset = destinationPixel * 32u;
    AtomicAdd64(voteOffset, LoadByte(originalPixels, sourceOffset) * weight);
    AtomicAdd64(voteOffset + 8u, LoadByte(originalPixels, sourceOffset + 1u) * weight);
    AtomicAdd64(voteOffset + 16u, LoadByte(originalPixels, sourceOffset + 2u) * weight);
    AtomicAdd64(voteOffset + 24u, weight);
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint fieldSourcePixel = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    uint pixelCount = imageWidth * imageHeight;
    if (fieldSourcePixel >= pixelCount || LoadByte(fieldSourceGlobal, fieldSourcePixel) != 0)
    {
        return;
    }

    bool fieldSourceIsMasked = LoadByte(fieldSourceInvalid, fieldSourcePixel) != 0;
    if ((sourceToTarget != 0 && !fieldSourceIsMasked) ||
        (sourceToTarget == 0 && fieldSourceIsMasked))
    {
        return;
    }

    uint3 link = nearestNeighborField.Load3(fieldSourcePixel * 12u);
    uint patchWidth = patchRadius * 2u + 1u;
    uint denominator = SsdScale * patchWidth * patchWidth;
    uint normalizedDistance = NormalizeRawDistance(link.z, denominator);
    uint weight = similarityWeights.Load(normalizedDistance * 4u);
    if (weight == 0)
    {
        return;
    }

    int sourceCenterY = (int)(fieldSourcePixel / imageWidth);
    int sourceCenterX = (int)(fieldSourcePixel % imageWidth);
    int matchedY = (int)link.x;
    int matchedX = (int)link.y;
    int radius = (int)patchRadius;
    for (int deltaY = -radius; deltaY <= radius; deltaY++)
    {
        int fieldSourceY = sourceCenterY + deltaY;
        int fieldTargetY = matchedY + deltaY;
        if (fieldSourceY < 0 || fieldSourceY >= (int)imageHeight ||
            fieldTargetY < 0 || fieldTargetY >= (int)imageHeight)
        {
            continue;
        }

        for (int deltaX = -radius; deltaX <= radius; deltaX++)
        {
            int fieldSourceX = sourceCenterX + deltaX;
            int fieldTargetX = matchedX + deltaX;
            if (fieldSourceX < 0 || fieldSourceX >= (int)imageWidth ||
                fieldTargetX < 0 || fieldTargetX >= (int)imageWidth)
            {
                continue;
            }

            uint fieldSourceSample = (uint)(fieldSourceY * (int)imageWidth + fieldSourceX);
            uint fieldTargetSample = (uint)(fieldTargetY * (int)imageWidth + fieldTargetX);
            if (LoadByte(fieldSourceGlobal, fieldSourceSample) != 0 ||
                LoadByte(fieldTargetGlobal, fieldTargetSample) != 0)
            {
                continue;
            }

            uint originalSourceSample = sourceToTarget != 0 ? fieldSourceSample : fieldTargetSample;
            uint destinationSample = sourceToTarget != 0 ? fieldTargetSample : fieldSourceSample;
            // Coarse masks validate the correspondence above. Pixel eligibility and RGB
            // come from the fine source when transferring votes to the next pyramid level.
            uint sourceY = originalSourceSample / imageWidth;
            uint sourceX = originalSourceSample % imageWidth;
            uint targetY = destinationSample / imageWidth;
            uint targetX = destinationSample % imageWidth;
            for (uint dy = 0; dy < voteScale; dy++)
            {
                for (uint dx = 0; dx < voteScale; dx++)
                {
                    uint sy = sourceY * voteScale + dy;
                    uint sx = sourceX * voteScale + dx;
                    uint ty = targetY * voteScale + dy;
                    uint tx = targetX * voteScale + dx;
                    if (sy >= voteHeight || sx >= voteWidth || ty >= voteHeight || tx >= voteWidth)
                    {
                        continue;
                    }

                    uint sourcePixel = sy * voteWidth + sx;
                    if (LoadByte(originalInvalid, sourcePixel) == 0)
                    {
                        AddWeightedPixel(ty * voteWidth + tx, sourcePixel, weight);
                    }
                }
            }
        }
    }
}
