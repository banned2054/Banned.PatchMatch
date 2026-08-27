namespace Banned.PatchMatch;

/// <summary>
/// 配置 PatchMatch 图像修复操作。<br/>
/// Configures a PatchMatch inpainting operation.
/// </summary>
public sealed class PatchMatchOptions
{
    internal static PatchMatchOptions Default { get; } = new();

    /// <summary>
    /// 获取补丁半径，实际补丁边长为 <c>2 * PatchRadius + 1</c>。<br/>
    /// Gets the patch radius. The actual patch edge length is <c>2 * PatchRadius + 1</c>.
    /// </summary>
    public int PatchRadius { get; init; } = 3;

    /// <summary>
    /// 获取用于初始化和搜索最近邻场的确定性随机种子。<br/>
    /// Gets the deterministic random seed used to initialize and search nearest-neighbor fields.
    /// </summary>
    public uint RandomSeed { get; init; } = 1212;

    internal void Validate(int width, int height)
    {
        if (PatchRadius < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PatchRadius), PatchRadius,
                                                  "The patch radius must be at least 1.");
        }

        if (PatchRadius >= width || PatchRadius >= height)
        {
            throw new ArgumentOutOfRangeException(nameof(PatchRadius), PatchRadius,
                                                  "The patch radius must be smaller than both image dimensions.");
        }
    }
}
