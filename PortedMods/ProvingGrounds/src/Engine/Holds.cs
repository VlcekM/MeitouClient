using Meitou.Simulation;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>Port only (task 8.4a, docs/divergences.md "Hold registry (task 8.4a)"): the one hold registry of a world (Core/Glue/HoldRegistry.cs) bound
/// to PgEngine's spike S10 adapters. One object per world (TownArena's, built again with every ProvingGroundsSystem); TownArena.Tick runs the one
/// re-assert pass per SlowWorld. A player character is never held (the AI never runs one, AiSystem.cs:366): <see cref="Hold"/> answers whether it
/// exists and records nothing. Sim thread, serial steps only.</summary>
sealed class Holds
{
    readonly HoldRegistry<CharacterId> registry = new() { FailureLog = m => ProvingGroundsMod.Ctx?.Log(m) };
    readonly Engine engine = new();

    /// <summary>The registry (the test verb's listing, the owners' queries).</summary>
    internal HoldRegistry<CharacterId> Registry => registry;

    /// <summary>C++ <c>TownAftercare::ControlAvailable()</c> (TownAftercare.cpp:1051, true once InstallHooks found its checkOrders / setBedMode
    /// exports): the members the hold writes resolve (<see cref="Members"/> part <c>Holds</c>: CharacterCold.Engaged, AiMind.Walking / Replan,
    /// TaskStart.Stand; checked by PgCheck hooks against 0.17.3 and PgCheck versions in every installed version). Read once per process; a
    /// missing member is logged once and every hold is refused.</summary>
    internal static bool ControlAvailable => controlAvailable ??= Resolve();

    static bool? controlAvailable;

    static bool Resolve()
    {
        var missing = Members.Missing(Part.Holds).ToList();
        if (missing.Count == 0) return true;
        ProvingGroundsMod.Ctx?.Log($"hold registry off: {string.Join(", ", missing)} not found; town medics, the bench and borrowed NPCs are off");
        return false;
    }

    /// <summary><paramref name="owner"/> holds the NPC <paramref name="c"/> (spike S10 the first time). A player character: true when it exists, nothing
    /// recorded. False when the hold's members are missing (<see cref="ControlAvailable"/>), the id is stale, or the hold threw (logged once).</summary>
    internal bool Hold(World world, CharacterId c, HoldOwner owner)
    {
        if (c.IsNone || !ControlAvailable) return false;
        if (PgEngine.IsPlayer(world, c)) return PgEngine.Exists(world, c);
        return registry.Hold(engine.Bind(world), c, owner);
    }

    /// <summary><paramref name="owner"/> lets go of <paramref name="c"/>; Engaged off only when no owner is left.</summary>
    internal void Release(World world, CharacterId c, HoldOwner owner)
    {
        if (c.IsNone) return;
        registry.Release(engine.Bind(world), c, owner);
    }

    /// <summary><paramref name="owner"/> lets go of everyone it holds.</summary>
    internal void ReleaseAll(World world, HoldOwner owner) => registry.ReleaseAll(engine.Bind(world), owner);

    /// <summary>The one re-assert pass of the SlowWorld (TownArena.Tick).</summary>
    internal void ReassertAll(World world) => registry.ReassertAll(engine.Bind(world));

    internal bool IsHeld(CharacterId c) => registry.IsHeld(c);
    internal bool IsHeldBy(CharacterId c, HoldOwner owner) => registry.IsHeldBy(c, owner);
    internal HoldOwner OwnersOf(CharacterId c) => registry.OwnersOf(c);

    /// <summary>The world changed: forget every hold (no actor access).</summary>
    internal void Clear() => registry.Clear();

    sealed class Engine : IHoldEngine<CharacterId>
    {
        World? world;
        internal Engine Bind(World w) { world = w; return this; }
        World W => world ?? throw new InvalidOperationException("Holds used before a world was bound");
        public bool Hold(CharacterId id) => PgEngine.HoldStill(W, id);
        public bool Reassert(CharacterId id)
        {
            if (!PgEngine.Exists(W, id)) return false;
            PgEngine.ReassertHold(W, id);
            return true;
        }
        public void Release(CharacterId id) => PgEngine.ReleaseHold(W, id);
    }
}
