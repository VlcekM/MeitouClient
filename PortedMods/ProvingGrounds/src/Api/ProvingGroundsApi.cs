// Proving Grounds for Meitou's public surface for other Meitou code mods (port only, task 10.1; docs/api.md says how to call it). Read it by
// reflection, so your mod never depends on ours:
//
//   static readonly Type? Pg = AppDomain.CurrentDomain.GetAssemblies()
//       .FirstOrDefault(a => a.GetName().Name == "ProvingGroundsMeitou")?.GetType("ProvingGrounds.ProvingGroundsApi");
//   static readonly Func<CharacterId, bool>? inBout =
//       Pg?.GetMethod("InBout", [typeof(CharacterId)])?.CreateDelegate<Func<CharacterId, bool>>();
//   bool sport = inBout?.Invoke(striker) == true && inBout(victim);
//
// Look it up on first use in a tick, not in Initialize (mods load in any order). Every member answers from one immutable view the arena
// publishes at the end of its tick, so it is safe from any thread, allocates nothing and never throws; before a game is loaded (or with the
// original Proving Grounds data missing, which switches this mod off) every answer is false. Version goes up when something is added; nothing
// is removed and nothing changes meaning: check Version >= n for what you need.
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds;

/// <summary>What other mods can ask Proving Grounds for Meitou (reflection; members only added, never changed).</summary>
public static class ProvingGroundsApi
{
    /// <summary>1: Build, Ready, InBout, IsTownFighter, IsTownFighterId.</summary>
    public const int Version = 1;

    /// <summary>The mod's version, e.g. "0.2.0".</summary>
    public static string Build => ProvingGroundsMod.Version;

    /// <summary>A game is loaded and the arena runs in it.</summary>
    public static bool Ready => View.Ready;

    /// <summary>
    /// The character is in one of the arena's bouts, or being walked to or from one: walking to the ring, fighting, benched between Teams 1v1
    /// bouts, knocked out in it, carried and treated by Scratch's medics in the aftercare, a match prisoner from its cage back into it. In the
    /// squad's own arena and in Scratch's town arena alike. Stays true for 30 s of game time after the arena lets the character go, so a
    /// bout's last blow (the knock-out) counted a moment later is still sport. A blow between two characters that are both InBout is part of
    /// the bout. False for <see cref="CharacterId.None"/>, a stale id (the slot reused by someone else), or before a game is loaded.
    /// </summary>
    public static bool InBout(CharacterId id) => !id.IsNone && View.InBout(id);

    /// <summary>One of Scratch's town fighters by name, as the game shows it (letter case and spaces around it ignored): the Bloodstamp
    /// characters of the arena's fighter templates and its unique fighters (Senn, Torka, Skarn, Ressa Vane, Bum-Per, Veyr). The arena's
    /// leaderboard shows them by name, so don't rename them. The roster is read every few seconds.</summary>
    public static bool IsTownFighter(string name) => View.IsTownFighter(name);

    /// <summary>The same test by character (exact: a reused slot is someone else).</summary>
    public static bool IsTownFighterId(CharacterId id) => !id.IsNone && View.IsTownFighterId(id);

    static ArenaApiView<CharacterId> View => Engine.ProvingGroundsSystem.Current?.ApiView ?? ArenaApiView<CharacterId>.NotReady;
}
