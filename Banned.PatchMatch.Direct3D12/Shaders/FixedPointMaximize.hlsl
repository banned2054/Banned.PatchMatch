#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "UAV(u0), " \
    "UAV(u1), " \
    "RootConstants(num32BitConstants=3, b0)"

ByteAddressBuffer targetPixels : register(t0);
ByteAddressBuffer targetGlobal : register(t1);
RWByteAddressBuffer votes : register(u0);
RWByteAddressBuffer outputPixels : register(u1);

cbuffer Constants : register(b0)
{
    uint imageWidth;
    uint imageHeight;
    uint dispatchWidth;
};

uint LoadByte(ByteAddressBuffer buffer, uint byteOffset)
{
    uint word = buffer.Load(byteOffset & ~3u);
    return (word >> ((byteOffset & 3u) * 8)) & 0xffu;
}

uint2 LoadAccumulator(uint byteOffset)
{
    return votes.Load2(byteOffset);
}

bool LessThanOrEqual64(uint2 left, uint2 right)
{
    return left.y < right.y || (left.y == right.y && left.x <= right.x);
}

bool Equal64(uint2 left, uint2 right)
{
    return left.x == right.x && left.y == right.y;
}

uint2 Multiply64BySmall(uint2 value, uint factor)
{
    uint lowLow = value.x & 0xffffu;
    uint lowHigh = value.x >> 16;
    uint product = lowLow * factor;
    uint resultLowLow = product & 0xffffu;
    uint carry = product >> 16;
    product = lowHigh * factor + carry;
    uint resultLowHigh = product & 0xffffu;
    uint resultHigh = value.y * factor + (product >> 16);
    return uint2(resultLowLow | (resultLowHigh << 16), resultHigh);
}

uint DivideAndRoundToEven(uint2 weightedChannel, uint2 weight)
{
    uint quotient = 0;
    for (int bit = 7; bit >= 0; bit--)
    {
        uint candidate = quotient | (1u << bit);
        if (LessThanOrEqual64(Multiply64BySmall(weight, candidate), weightedChannel))
        {
            quotient = candidate;
        }
    }

    uint2 twiceChannel = Multiply64BySmall(weightedChannel, 2u);
    uint2 midpoint = Multiply64BySmall(weight, quotient * 2u + 1u);
    if (LessThanOrEqual64(midpoint, twiceChannel))
    {
        if (!Equal64(midpoint, twiceChannel) || (quotient & 1u) != 0)
        {
            quotient++;
        }
    }

    return min(quotient, 255u);
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint pixel = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    if (pixel >= imageWidth * imageHeight)
    {
        return;
    }

    uint targetOffset = pixel * 3u;
    uint red = LoadByte(targetPixels, targetOffset);
    uint green = LoadByte(targetPixels, targetOffset + 1u);
    uint blue = LoadByte(targetPixels, targetOffset + 2u);
    if (LoadByte(targetGlobal, pixel) == 0)
    {
        uint voteOffset = pixel * 32u;
        uint2 weight = LoadAccumulator(voteOffset + 24u);
        if (weight.x != 0 || weight.y != 0)
        {
            red = DivideAndRoundToEven(LoadAccumulator(voteOffset), weight);
            green = DivideAndRoundToEven(LoadAccumulator(voteOffset + 8u), weight);
            blue = DivideAndRoundToEven(LoadAccumulator(voteOffset + 16u), weight);
        }
    }

    outputPixels.Store(pixel * 4u, red | (green << 8) | (blue << 16));
}
