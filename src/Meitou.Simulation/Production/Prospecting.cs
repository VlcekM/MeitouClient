using System.Numerics;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

/// <summary>
/// The HUD's Prospect button: the character stands where it is and prospects (the original's <c>Task_Prospecting</c>, order 150, docs/game/ai-tasks.md "Prospecting"). The host names the one nearest the
/// camera; a character that already prospects, is down, fighting, carried, caged or swimming ignores it.
/// </summary>
public sealed record ProspectOrder(CharacterId Character) : OrderCommand;

/// <summary>
/// One line of the results window: a resource or crop coverage and its value (1 = 100 %).
/// </summary>
public readonly record struct ProspectLine(string Name, float Value)
{
    /// <summary>
    /// The number shown: the value times 100, cut to an int (<b>Verified</b>, <c>FUN_14049d850</c>).
    /// </summary>
    public int Percent => (int)(Value * 100);
}

/// <summary>
/// What a finished prospecting found (docs/game/ui-screens.md "Prospecting"): the spot, the prospector and the window's lines. Display only: the host reads <see cref="World.Prospected"/>
/// and opens the results window when <see cref="Serial"/> is new; nothing in the simulation reads it.
/// </summary>
/// <param name="Serial">1, 2, 3... one per finished prospecting of this world.</param>
/// <param name="Position">Where the prospector stood.</param>
/// <param name="Prospector">Its name.</param>
/// <param name="Skill">Its science stat, cut to an int (the window's "Prospector Skill").</param>
/// <param name="Zone">The BIOME_GROUP's name at the spot ("" without an areas map).</param>
/// <param name="Altitude">The height of the spot, cut to an int.</param>
/// <param name="AreaMetres">"Area Size": <c>int((science / 100 x 6000 + 2000) x 0.2)</c>.</param>
/// <param name="Environment">Green, Arid and Swamp, only those above 0 (empty: "Dead").</param>
/// <param name="Resources">Water, Fertility, Stone, Iron, Copper in the window's order.</param>
/// <param name="Map">The samples the window's map is drawn from (null: none taken).</param>
/// <param name="WindSpeed">The current wind of the spot's weather region in units/s, 0 without one (the window shows it times 0.22369362 as mph).</param>
public sealed record ProspectReport(long Serial, Vector3 Position, string Prospector, int Skill, string Zone, int Altitude, int AreaMetres,
    IReadOnlyList<ProspectLine> Environment, IReadOnlyList<ProspectLine> Resources, ProspectMap? Map = null, float WindSpeed = 0);

/// <summary>
/// The ground under the results window's map (<b>Verified</b>, <c>FUN_14049d850</c> / <c>FUN_14049cd60</c>, docs/game/ui-screens.md "Prospecting"): <see cref="Size"/> x <see cref="Size"/> samples over the
/// square of side <see cref="Extent"/> whose -X/-Z corner is <see cref="X"/>, <see cref="Z"/>, each taken at its cell's centre, rows from -Z.
/// </summary>
/// <param name="Heights">The terrain height of each sample.</param>
/// <param name="Fields">Per resource row: the value of each sample (Water, Fertility, Stone); null for a rock resource.</param>
/// <param name="Rocks">Per resource row: the rocks within <see cref="RockRange"/> x <see cref="Extent"/> of the spot with their own yields (Iron, Copper); empty for a field resource.</param>
public sealed record ProspectMap(int Size, float X, float Z, float Extent, float[] Heights, IReadOnlyList<float[]?> Fields, IReadOnlyList<IReadOnlyList<ProspectRock>> Rocks)
{
    /// <summary>
    /// The rock search radius as a share of the side (<b>Verified</b>: squared 0.6 x side, <c>FUN_14049cd60</c>).
    /// </summary>
    public const float RockRange = 0.6f;
}

/// <summary>
/// A rock on the prospecting map: where it stands and its own yield (the value a mine next to it gets).
/// </summary>
public readonly record struct ProspectRock(float X, float Z, float Value);