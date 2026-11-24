#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "UAV(u0), " \
    "UAV(u1), " \
    "RootConstants(num32BitConstants=4, b0)"

ByteAddressBuffer sourceInvalid : register(t0);
ByteAddressBuffer sourceGlobal : register(t1);
RWByteAddressBuffer sourceToTargetField : register(u0);
RWByteAddressBuffer targetToSourceField : register(u1);

cbuffer Constants : register(b0)
{
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

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint pixel = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    if (pixel >= imageWidth * imageHeight)
    {
        return;
    }

    int centerY = (int)(pixel / imageWidth);
    int centerX = (int)(pixel % imageWidth);
    int radius = (int)patchRadius;
    for (int deltaY = -radius; deltaY <= radius; deltaY++)
    {
        int candidateY = centerY + deltaY;
        if (candidateY < 0 || candidateY >= (int)imageHeight)
        {
            continue;
        }

        for (int deltaX = -radius; deltaX <= radius; deltaX++)
        {
            int candidateX = centerX + deltaX;
            if (candidateX < 0 || candidateX >= (int)imageWidth)
            {
                continue;
            }

            uint candidate = (uint)(candidateY * (int)imageWidth + candidateX);
            if (LoadByte(sourceInvalid, candidate) != 0 && LoadByte(sourceGlobal, candidate) == 0)
            {
                return;
            }
        }
    }

    uint fieldOffset = pixel * 12u;
    uint3 identity = uint3((uint)centerY, (uint)centerX, 0u);
    sourceToTargetField.Store3(fieldOffset, identity);
    targetToSourceField.Store3(fieldOffset, identity);
}
