using Meitou.Simulation.Items;

namespace Meitou.Simulation.Combat;

/// <summary>
/// One worn armour piece as a hit is about to go through it (docs/modding.md "Armour"): the tick, the hit's source, the wearer, the worn item the
/// piece was built from (null for a piece without one, such as a test fighter's), its place in the wearer's stack, and the piece with its defences.
/// </summary>
public readonly record struct ArmourAsk(long Tick, CombatHitSource Source, CharacterId Wearer, ItemInstance? Item, int Index, ArmourPiece Piece);

/// <summary>
/// A system asked, for every hit on a character wearing armour, about each worn piece before the hit goes through the stack (docs/modding.md
/// "Armour"). It answers the piece to use: <see cref="ArmourAsk.Piece"/> unchanged, or with lower or higher cut, blunt or pierce defence or
/// cut-into-stun (worn gear, enchanted gear). Filters run serially where the hit lands, in system order, each seeing the piece as the earlier ones
/// left it; they must answer from the world and the ask alone and change nothing. An answer with a defence that is not finite or outside the
/// game's range (cut 0 to 0.9, blunt 0 to 0.8, pierce from 0, cut-into-stun 0 to 1), or a throw, keeps the piece as it came and disables the
/// filter for that world; the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IArmourFilter : ITickSystem
{
    /// <summary>
    /// The piece to use for this hit.
    /// </summary>
    ArmourPiece Piece(World world, in ArmourAsk ask);
}
