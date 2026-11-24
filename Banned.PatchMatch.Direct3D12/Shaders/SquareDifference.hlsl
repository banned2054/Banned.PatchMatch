#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "UAV(u0), " \
    "RootConstants(num32BitConstants=2, b0)"

ByteAddressBuffer leftValues : register(t0);
ByteAddressBuffer rightValues : register(t1);
RWByteAddressBuffer differences : register(u0);

cbuffer Constants : register(b0)
{
    uint valueCount;
    uint dispatchWidth;
};

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint index = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    if (index >= valueCount)
    {
        return;
    }

    uint byteOffset = index & ~3u;
    uint shift = (index & 3u) * 8;
    uint left = (leftValues.Load(byteOffset) >> shift) & 0xffu;
    uint right = (rightValues.Load(byteOffset) >> shift) & 0xffu;
    int delta = (int)left - (int)right;
    differences.Store(index * 4, (uint)(delta * delta));
}
