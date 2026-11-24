#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "UAV(u0), " \
    "RootConstants(num32BitConstants=2, b0)"

ByteAddressBuffer packedPixels : register(t0);
RWByteAddressBuffer tightPixels : register(u0);

cbuffer Constants : register(b0)
{
    uint imageByteCount;
    uint dispatchWidth;
};

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint outputWord = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    uint outputOffset = outputWord * 4u;
    if (outputOffset >= imageByteCount)
    {
        return;
    }

    uint packedWord = 0;
    for (uint byteIndex = 0; byteIndex < 4u; byteIndex++)
    {
        uint imageByte = outputOffset + byteIndex;
        if (imageByte >= imageByteCount)
        {
            break;
        }

        uint pixel = imageByte / 3u;
        uint channel = imageByte - pixel * 3u;
        uint pixelChannels = packedPixels.Load(pixel * 4u);
        packedWord |= ((pixelChannels >> (channel * 8u)) & 0xffu) << (byteIndex * 8u);
    }

    tightPixels.Store(outputOffset, packedWord);
}
