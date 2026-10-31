using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Cut-off heads (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): drawn by the character renderer, not the simulation's. When a character the
/// renderer drew with its head last frame is listed headless, its head starts falling from where it was: the look's <see cref="CharacterAppearance.SeveredHead"/>, holding
/// the bones of that last frame (<see cref="CharacterInstance.FreezeFrom"/>), placed by the body's placement then, turned about the head bone and dropped to the corpse's
/// ground. Timed by the shown tick as the severed limbs are (still while paused). Not saved: a load or a new game clears them; past the dropped-items limit the oldest goes.
/// </summary>
sealed partial class GameHost
{
    sealed class FallingHead
    {
        public required long Key, From;
        public required CharacterAppearance Look;
        /// <summary>The body's placement and its head bone's place when it was cut.</summary>
        public required Matrix4x4 World;
        public required Vector3 Start, Rest, Axis;
        public required double Tick;
        public required float Flight, Spin;
        public required ulong Blood;
    }

    readonly List<FallingHead> heads = [];
    /// <summary>The characters whose cut was looked at (a head spawned, or none to spawn: drawn headless already, or not drawn), for <see cref="headsEpoch"/>.</summary>
    readonly HashSet<CharacterId> headsSeen = [];
    long headsEpoch = -1;
    /// <summary>How far above the corpse's feet the head bone rests (about a head's half height).</summary>
    const float HeadRestHeight = 1.3f;

    /// <summary>A headless character listed this frame: its head falls if the renderer drew it with one in its last update.</summary>
    void CutHead(CharacterSnapshot c, CharacterAppearance look)
    {
        if (!look.Headless || gpu.Characters is not { } characters || !headsSeen.Add(c.Id)) return;
        long from = DrawKey(c.Id);
        if (characters.LastDrawn(from, "Bip01 Head") is not ({ } world, { Headless: false } before, var start)) return;
        ulong roll = unchecked((ulong)from * 0x9E3779B97F4A7C15UL ^ (ulong)session.ShownTick * 0xBF58476D1CE4E5B9UL);   // a look, not the simulation's: no world roll
        roll ^= roll >> 31;
        float angle = (roll & 0xFFFF) / 65536f * MathF.Tau, reach = 3 + ((roll >> 16) & 0xFFFF) / 65536f * 4;
        var rest = new Vector3(start.X + MathF.Cos(angle) * reach, c.Position.Y + HeadRestHeight, start.Z + MathF.Sin(angle) * reach);
        heads.Add(new FallingHead
        {
            Key = from ^ (1L << 54), From = from, Look = before.SeveredHead(), World = world, Start = start, Rest = rest,
            Axis = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)), Tick = session.ShownTick + session.SimulationAlpha,
            Flight = MathF.Sqrt(2 * MathF.Max(start.Y - rest.Y, 0) / FallGravity), Spin = 2.5f + ((roll >> 32) & 0xFFFF) / 65536f * 3,
            Blood = c.BloodAmounts,
        });
        while (heads.Count > Math.Max(config.GroundItemLimit, 1)) heads.RemoveAt(0);
    }

    /// <summary>The heads, each placed where its fall has it now: the head bone along the drop, the whole head turned about it until it lands.</summary>
    void AddHeads(CharacterDrawList list)
    {
        if (headsEpoch != worldEpoch) { heads.Clear(); headsSeen.Clear(); headsEpoch = worldEpoch; }
        double clock = session.ShownTick + session.SimulationAlpha;
        float tick = session.World.TickSeconds;
        foreach (var h in heads)
        {
            float t = MathF.Min(MathF.Max(0, (float)((clock - h.Tick) * tick)), h.Flight), s = h.Flight > 0 ? t / h.Flight : 1;
            var at = new Vector3(h.Start.X + (h.Rest.X - h.Start.X) * s, h.Start.Y - 0.5f * FallGravity * t * t, h.Start.Z + (h.Rest.Z - h.Start.Z) * s);
            var world = h.World * Matrix4x4.CreateTranslation(-h.Start) * Matrix4x4.CreateFromAxisAngle(h.Axis, h.Spin * t) * Matrix4x4.CreateTranslation(at);
            list.Add(new CharacterInstance(h.Key, h.Look, at, 0, []) { World = world, FreezeFrom = h.From, Blood = h.Blood });
        }
    }
}
