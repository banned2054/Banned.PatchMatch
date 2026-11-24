namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 阶段 2 内部最近邻链接快照。<br/>
/// Internal nearest-neighbor link snapshot for phase 2.
/// </summary>
internal readonly record struct NearestNeighborLink(int TargetY, int TargetX, int Distance);
