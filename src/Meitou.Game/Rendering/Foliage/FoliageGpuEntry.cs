namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// One group's records (<c>Start</c> and <c>Length</c> in <c>FoliageGroup.Instances</c>: all of them, or a rock's run of one biome row) in a batch;
/// <c>ImpostorMesh</c>: a rock's mesh run whose row has an atlas (its chunks carry the transition).
/// </summary>
readonly record struct FoliageGpuEntry(int Batch, FoliageGroup Group, int Start, int Length, bool ImpostorMesh, int Level = 0);
