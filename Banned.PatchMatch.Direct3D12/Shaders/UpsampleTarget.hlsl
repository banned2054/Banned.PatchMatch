#define ROOT_SIGNATURE \
    "RootFlags(0), " \
    "SRV(t0), " \
    "SRV(t1), " \
    "SRV(t2), " \
    "SRV(t3), " \
    "UAV(u0), " \
    "UAV(u1), " \
    "UAV(u2), " \
    "RootConstants(num32BitConstants=5, b0)"

ByteAddressBuffer coarseTargetPixels : register(t0);
ByteAddressBuffer coarseTargetInvalid : register(t1);
ByteAddressBuffer coarseTargetGlobal : register(t2);
ByteAddressBuffer fineSourceGlobal : register(t3);
RWByteAddressBuffer fineTargetPixels : register(u0);
RWByteAddressBuffer fineTargetInvalid : register(u1);
RWByteAddressBuffer fineTargetGlobal : register(u2);

cbuffer Constants : register(b0)
{
    uint coarseWidth;
    uint coarseHeight;
    uint fineWidth;
    uint fineHeight;
    uint dispatchWidth;
};

uint LoadByte(ByteAddressBuffer buffer, uint byteOffset)
{
    uint word = buffer.Load(byteOffset & ~3u);
    return (word >> ((byteOffset & 3u) * 8)) & 0xffu;
}

// Nearest-neighbor lookup: the coarse target pixel that covers a fine pixel.
uint CoarsePixel(uint finePixel)
{
    uint coarseY = (finePixel / fineWidth) * coarseHeight / fineHeight;
    uint coarseX = (finePixel % fineWidth) * coarseWidth / fineWidth;
    return coarseY * coarseWidth + coarseX;
}

// MaskedImage.Upsample derives the fine hole mask from the coarse pixel: a coarse hole or a globally
// excluded coarse pixel both make the fine pixel a hole. This alone decides whether a color is copied.
uint IsCoarseHole(uint coarsePixel)
{
    uint invalid = LoadByte(coarseTargetInvalid, coarsePixel) | LoadByte(coarseTargetGlobal, coarsePixel);
    return invalid != 0u ? 1u : 0u;
}

[RootSignature(ROOT_SIGNATURE)]
[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint thread = dispatchThreadId.x + dispatchThreadId.y * dispatchWidth;
    uint pixelCount = fineWidth * fineHeight;
    uint imageWordCount = (pixelCount * 3u + 3u) / 4u;
    uint maskWordCount = (pixelCount + 3u) / 4u;
    if (thread >= imageWordCount + maskWordCount)
    {
        return;
    }

    // Image threads pack one 4-byte word of the tightly packed RGB output. Hole pixels keep 0.
    if (thread < imageWordCount)
    {
        uint byteOffset = thread * 4u;
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
            uint coarsePixel = CoarsePixel(pixel);
            if (IsCoarseHole(coarsePixel) == 0u)
            {
                packedWord |= LoadByte(coarseTargetPixels, coarsePixel * 3u + channel) << (byteIndex * 8u);
            }
        }

        fineTargetPixels.Store(byteOffset, packedWord);
        return;
    }

    // Mask threads pack one 4-byte word of the merged invalid mask and of the replaced global mask.
    // The workset only stores the merged mask (hole | global), so the fine source's global mask joins
    // the coarse-derived holes here. It never affects which colors were copied above.
    uint maskOffset = (thread - imageWordCount) * 4u;
    uint invalidWord = 0u;
    uint globalWord = 0u;
    for (uint byteIndex = 0u; byteIndex < 4u; byteIndex++)
    {
        uint pixel = maskOffset + byteIndex;
        if (pixel >= pixelCount)
        {
            break;
        }

        uint hole = IsCoarseHole(CoarsePixel(pixel));
        uint globallyMasked = LoadByte(fineSourceGlobal, pixel);
        invalidWord |= ((hole | globallyMasked) != 0u ? 1u : 0u) << (byteIndex * 8u);
        globalWord |= globallyMasked << (byteIndex * 8u);
    }

    fineTargetInvalid.Store(maskOffset, invalidWord);
    fineTargetGlobal.Store(maskOffset, globalWord);
}
