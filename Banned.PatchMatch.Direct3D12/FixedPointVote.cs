namespace Banned.PatchMatch.Direct3D12;

/// <summary>
/// 阶段 2 定点投票缓冲的单像素快照。各通道和权重均使用相同定点比例。<br/>
/// Per-pixel snapshot of the phase-2 fixed-point vote buffer. Channels and weight use the same fixed-point scale.
/// </summary>
internal readonly record struct FixedPointVote(ulong Red, ulong Green, ulong Blue, ulong Weight);
