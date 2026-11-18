using System.Globalization;
using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ArenaRingGuard.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.4 (PA §2.10, CAP 5 "Ring guard at
// any speed"). Every function of ArenaRingGuard.cpp, in order with its line range, is in Core/Glue/RingGuardLogic.cs over IRingGuardHost
// (tested with a fake, Tests/Glue/RingGuardLogicTests.cs); the header's arithmetic is Core/ArenaRingGuard.cs (M1). This file binds the world.
// Meitou 0.17.3 (tag v0.17.3, fa34a1b). Core's class of this name is Core.ArenaRingGuard; never the bare name in this namespace.
//
// Engine calls of the C++ and what replaces them (E1):
//   SparSession::IsActive / GetParticipantCount / GetParticipant / IsEliminated (:67, :88-98) ... src/Game/SparSession.cs (task 3.3)
//   hand g_site, g_site.isValid(), getBuilding(), site->isValid(), site->getPosition() (:29, :69-71, :82) ... RingSite (the centre and an
//       optional still-there check, below); M4 hands the staged site building (see SetArena)
//   ArenaIdentity::IsBanner / IsSmallArena (RingScaleForSite :43-44) ... RingSite.Kind (M4: task 4.1's ArenaIdentity of the site building)
//   BalanceTuning::Get() ringNearRadius / ringOutRadius / ringReturnRadius (:73-76) ... Core.BalanceTuning.Get()
//   GetTickCount() (:83) ... PgClock.NowMs
//   c->isValid() (:95), c->getMovement() (:100), c->getPosition() (:103) ... PgEngine.Exists / Position (a Meitou character always moves)
//   movement->combatMover.hasForcedWP = false + movement->_setPositionSimple(target) (:123-124) ... PgEngine.TryPlaceGroundedKeepingTask
//       (CharacterTable.Place with the character's own yaw and task, CharacterTable.cs:1524, on the ground under the spot and only where it is
//       walkable, IWalkability.TryStand); Meitou has no forced waypoint to clear
//   movement->combatMover.setForcedWP(target) (:134) ... none in Meitou for a fighter: a step inward (2.5 units x the ring's scale) with
//       PgEngine.TryPlaceGroundedKeepingTask
//       (divergences.md)
//   PGLog::Debug(line) (:126-128, :136-138) ... PGLog.Debug, the C++ texts with %.0f / %.2f through CMath.FormatF
// Port only: a downed (unconscious or dead, KOWatcher's test) or carried participant is never moved (the ragdoll owns the body, CAP 5).
// Callers (C++ -> port): ArenaUI.cpp:3163 Tick = the frame loop's ArenaRingGuard.Tick slot (ProvingGroundsSystem.SlowWorld, serial);
// ArenaIngress.cpp:1343 SetArena(site) (M4 task 4.3, the player arena at ingress start) and TownArena.cpp:1715 SetArena(arena) (M7 task 7.3);
// both through PgIngressHost.SetRingSite (the town through ArenaIngress.SetRingSite); the TEST-ONLY `pg ring` (divergences.md "Test-only commands")
// still sets it for ring.sh and arena-ingress.sh.

/// <summary>The site a bout's ring is drawn around: C++ the <c>Building*</c> handed to <c>SetArena</c>. <see cref="Centre"/> is the building's
/// origin (<c>site-&gt;getPosition()</c>, read for x and z only); <see cref="StillThere"/> answers the C++ handle's validity each tick (null:
/// always there, a fixed test point); <see cref="Kind"/> is ArenaIdentity's banner / small arena reading of the building.</summary>
internal sealed record RingSite(string Label, Vector3 Centre, RingSiteKind Kind, Func<World, bool>? StillThere = null);

/// <summary>The glue of the original ArenaRingGuard.cpp: keeps the running spar's fighters inside the site's ring. One object per world.</summary>
sealed class ArenaRingGuard(SparSession spar, PgClock clock)
{
    readonly RingGuardLogic<CharacterId> logic = new();
    readonly Host host = new(spar, clock);
    RingSite? site;

    /// <summary>The site in force (null = none).</summary>
    internal RingSite? Site => site;
    internal RingGuardLogic<CharacterId> Logic => logic;

    // C++: ArenaRingGuard.cpp:53-59 (RingGuardLogic.SetArena). M4 (task 4.3, ArenaIngress.cpp:1343) calls this at ingress start with the staged
    // site building: Centre = the building's Position, StillThere = the building still exists and is not destroyed, Kind = ArenaIdentity's
    // (task 4.1); M7 (task 7.3, TownArena.cpp:1715) with the town arena; the test-only `pg ring` too. Like the C++, it stays
    // set after the bout (the tick is gated on SparSession::IsActive) until the next SetArena.
    internal void SetArena(RingSite? newSite)
    {
        site = newSite;
        logic.SetArena(newSite is not null, newSite?.Kind ?? RingSiteKind.Arena);
    }

    // C++: ArenaRingGuard.cpp:61-143 (RingGuardLogic.Tick). Called from ArenaUI.cpp ForgottenGUI_update_hook (:3163), once for town and player bouts.
    internal void Tick(World world)
    {
        if (!spar.IsActive(world) || site is null) return;   // the C++'s first checks (:67-69), before binding anything
        host.Bind(world, site);
        logic.Tick(host);
    }

    /// <summary>The F10 phase row: the site and, since it was set, the corrections made and the fighters left (port-only counts; a fighter
    /// left counts once per second it stays so).</summary>
    internal string Phase => site is null ? "no ring"
        : string.Create(CultureInfo.InvariantCulture, $"ring at {site.Label} (scale {CMath.FormatF(logic.RingScale, 2)}): {logic.OutCorrections} returned, {logic.NearCorrections} stepped in; left: {logic.DownedSkips} downed, {logic.UnwalkableSkips} unwalkable spot (per fighter-second)")
            + (spar.Running ? "" : ", idle");

    /// <summary>Port only: unconscious or dead (KOWatcher's down test) or carried: the ragdoll's body, never moved (CAP 5).</summary>
    static bool Downed(World world, CharacterId c) =>
        PgEngine.IsDead(world, c) || PgEngine.IsUnconscious(world, c) || PgEngine.IsCarried(world, c);

    /// <summary>The world-side reads and writes of the tick. Bound per tick; allocates nothing.</summary>
    sealed class Host(SparSession spar, PgClock clock) : IRingGuardHost<CharacterId>
    {
        World world = null!;
        RingSite site = null!;

        internal void Bind(World w, RingSite s) { world = w; site = s; }

        public bool SparActive => spar.IsActive(world);
        public bool TryGetSiteCentre(out float x, out float y, out float z)
        {
            var c = site.Centre;
            x = c.X; y = c.Y; z = c.Z;
            return site.StillThere?.Invoke(world) ?? true;
        }
        public int ParticipantCount => spar.GetParticipantCount();
        public CharacterId Participant(int i) => spar.GetParticipant(i) ?? CharacterId.None;
        public bool IsNone(CharacterId c) => c.IsNone;
        public bool Exists(CharacterId c) => PgEngine.Exists(world, c);
        public bool IsEliminated(CharacterId c) => spar.IsEliminated(world, c);
        public bool TryGetPosition(CharacterId c, out float x, out float y, out float z)
        {
            if (PgEngine.Position(world, c) is { } p) { x = p.X; y = p.Y; z = p.Z; return true; }
            x = y = z = 0;
            return false;
        }
        public bool IsDowned(CharacterId c) => Downed(world, c);
        public bool TryPlaceKeepingTask(CharacterId c, float x, float nearY, float z) => PgEngine.TryPlaceGroundedKeepingTask(world, c, x, nearY, z);
        public uint NowMs => clock.NowMs;
        public void Debug(string line) => PGLog.Debug(line);
    }

    // ---------- TEST ONLY: pg ring (task 3.4, until M4 registers the staged site) ----------

    /// <summary>TEST ONLY <c>pg ring ...</c>: sets, clears or reads the ring, or pushes a fighter outward from its centre. Answers one PG-RING
    /// JSON line (also returned). Words:
    /// <list type="bullet">
    /// <item><c>pg ring at &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;arena|small|banner&gt; [scale &lt;f&gt;]</c>: a fixed point as the site.</item>
    /// <item><c>pg ring around &lt;id...&gt; &lt;arena|small|banner&gt; [scale &lt;f&gt;]</c>: the mean position of those characters (x, y, z).</item>
    /// <item><c>pg ring off</c>: no site (SetArena(null)).</item>
    /// <item><c>pg ring status</c>: the site, radii, counts and each participant's distance.</item>
    /// <item><c>pg ring push &lt;id&gt; &lt;distance&gt; [&lt;bearing degrees&gt;]</c>: puts that character at &lt;distance&gt; from the centre on its own
    ///   bearing (or the one given, from +x toward +z; then, where the guard acts, the guard's return spot on it must take a stand, see below),
    ///   on the ground under the spot
    ///   at the ring's floor (the centre's height as the floor hint, then, where nothing stands there, the character's own, as the guard's;
    ///   task 4.8: a building's origin can lie under its floor, Scratch's town Arena 183), keeping its task
    ///   (the same grounded, walkable-only write the guard makes), so a check can see the guard bring it back. Refused for a downed or carried
    ///   body, a distance over twice the out radius (1000 when the ring is inert) and an unwalkable spot. The return spot (on the given
    ///   bearing at the return radius) must take a stand (<c>TryStand</c> from the pushed spot's ground height, the guard's hint) only when the
    ///   push lands where the guard acts on it (at or beyond the near radius: Core/Glue/RingPushRule.cs, task 4.8); a push inside the near
    ///   line (an ingress check moving a fighter within the ring) needs only its own spot.</item>
    /// <item><c>pg ring sample [near &lt;id&gt;] &lt;radius...&gt;</c> (task 4.8, a measurement): at each radius from the centre, on 36 bearings
    ///   (every 10 degrees from +x toward +z), <c>IsWalkable</c> and <c>TryStand</c> with the centre's height as the floor hint (what the push
    ///   uses) and, with <c>near &lt;id&gt;</c>, with that character's height (what the guard uses: the fighter's own). Moves nobody; answers
    ///   the PG-RING line with a <c>sample</c> object. At most 16 radii. A height that is not finite, and the height of a failed stand (only
    ///   the fallback there), is written as <c>null</c>.</item>
    /// </list>
    /// <c>scale</c> replaces the kind's scale (no C++ counterpart: a small ring for a live check).</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0] : "status";
        switch (verb)
        {
            case "off":
                SetArena(null);
                return Answer(world, "off", true, null);
            case "at" or "around":
            {
                var words = args.Skip(1).ToList();
                float? scale = null;
                int si = words.IndexOf("scale");
                if (si >= 0)
                {
                    if (si + 1 >= words.Count || !float.TryParse(words[si + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s)
                        || !float.IsFinite(s) || s < 0) return Answer(world, verb, false, "scale needs a number >= 0");
                    scale = s;
                    words.RemoveRange(si, 2);
                }
                if (words.Count < 2 || !TryKind(words[^1], out var kind)) return Answer(world, verb, false, "usage pg ring at <x> <y> <z> <arena|small|banner> [scale <f>] | pg ring around <id...> <arena|small|banner> [scale <f>]");
                words.RemoveAt(words.Count - 1);
                Vector3 centre;
                string label;
                if (verb == "at")
                {
                    if (words.Count != 3 || !TryF(words[0], out float x) || !TryF(words[1], out float y) || !TryF(words[2], out float z))
                        return Answer(world, verb, false, "usage pg ring at <x> <y> <z> <arena|small|banner> [scale <f>]");
                    centre = new Vector3(x, y, z);
                    label = "a point";
                }
                else
                {
                    var sum = Vector3.Zero;
                    foreach (string w in words)
                    {
                        if (!CharacterIdText.TryParse(w, out var id) || PgEngine.Position(world, id) is not { } p)
                            return Answer(world, verb, false, "no character " + w);
                        sum += p;
                    }
                    centre = sum / words.Count;
                    label = "the mean of " + words.Count.ToString(CultureInfo.InvariantCulture) + " characters";
                }
                SetArena(new RingSite(label, centre, kind));
                if (scale is { } sc) logic.OverrideScaleForTest(sc);
                return Answer(world, verb, true, null);
            }
            case "push":
            {
                if (site is null) return Answer(world, verb, false, "no ring");
                if (args.Count < 3 || !CharacterIdText.TryParse(args[1], out var id) || !TryF(args[2], out float d) || d < 0)
                    return Answer(world, verb, false, "usage pg ring push <id> <distance> [<bearing degrees>]");
                float? bearing = null;
                if (args.Count > 3) { if (!TryF(args[3], out float deg)) return Answer(world, verb, false, "bearing needs a number"); bearing = deg; }
                if (PgEngine.Position(world, id) is not { } p) return Answer(world, verb, false, "no character " + args[1]);
                if (Downed(world, id)) return Answer(world, verb, false, "downed or carried: the ragdoll's body");
                var t = BalanceTuning.Get();
                float outR = (float)t.RingOutRadius * logic.RingScale;
                float cap = outR > 0 ? 2 * outR : 1000f;
                if (d > cap) return Answer(world, verb, false, "distance over " + CMath.FormatG(cap, 9));
                // the bearing: the character's own from the centre, or the one given (degrees from +x toward +z)
                float dx = p.X - site.Centre.X, dz = p.Z - site.Centre.Z;
                if (bearing is { } b) { dx = MathF.Cos(b * MathF.PI / 180f); dz = MathF.Sin(b * MathF.PI / 180f); }
                // the floor hint: the site centre's height first (the ring's floor for a site set around fighters, ring.sh), then the character's
                // own (the guard's hint for a fighter; RingPushRule.FloorHints). Task 4.8: a building's origin can lie under its floor: at
                // Scratch's town Arena 183, 25-48 units, so with the centre's height no spot on 36 bearings at 100-147.5 out stood (pg ring
                // sample; divergences.md)
                Core.ArenaRingGuard.RingReturnPoint(dx, dz, d, out float rx, out float rz);
                Core.ArenaRingGuard.RingReturnPoint(dx, dz, (float)t.RingReturnRadius * logic.RingScale, out float qx, out float qz);
                // with a bearing given and the push landing where the guard acts (near band or outside), the spot the guard returns to on that
                // bearing must take a stand too, from the pushed height (the guard's hint for the fighter there), so a check exercises a return
                // (a push inside the near line is never corrected)
                bool needsReturn = RingPushRule.NeedsWalkableReturn(bearing is not null, logic.RingScale, d, (float)t.RingNearRadius * logic.RingScale, outR);
                switch (RingPushRule.ChoosePushFloor(site.Centre.Y, p.Y, needsReturn,
                    (float h, out float gy) => PgEngine.TryStandAt(world, site.Centre.X + rx, h, site.Centre.Z + rz, out gy),
                    (float h, out float gy) => PgEngine.TryStandAt(world, site.Centre.X + qx, h, site.Centre.Z + qz, out gy),
                    out float floorHint))
                {
                    case RingPushRule.PushRefusal.SpotNotWalkable: return Answer(world, verb, false, "spot not walkable");
                    case RingPushRule.PushRefusal.ReturnSpotNotWalkable: return Answer(world, verb, false, "return spot not walkable");
                }
                if (!PgEngine.TryPlaceGroundedKeepingTask(world, id, site.Centre.X + rx, floorHint, site.Centre.Z + rz))   // the guard's own write
                    return Answer(world, verb, false, "spot not walkable");
                return Answer(world, verb, true, null, ",\"pushed\":" + PGLog.Quote(CharacterIdText.Format(id)));
            }
            case "sample":
                return Sample(world, args);
            case "status":
                return Answer(world, verb, true, null);
            default:
                return Answer(world, verb, false, "usage pg ring <at|around|off|status|push|sample> ...");
        }
    }

    /// <summary>TEST ONLY <c>pg ring sample</c> (see <see cref="Command"/>): walkability around the site's centre, nobody moved.</summary>
    string Sample(World world, IReadOnlyList<string> args)
    {
        if (site is null) return Answer(world, "sample", false, "no ring");
        var words = args.Skip(1).ToList();
        float? nearY = null;
        int ni = words.IndexOf("near");
        if (ni >= 0)
        {
            if (ni + 1 >= words.Count || !CharacterIdText.TryParse(words[ni + 1], out var nid) || PgEngine.Position(world, nid) is not { } np)
                return Answer(world, "sample", false, "near needs a character id");
            nearY = np.Y;
            words.RemoveRange(ni, 2);
        }
        if (words.Count == 0) return Answer(world, "sample", false, "usage pg ring sample [near <id>] <radius...>");
        if (words.Count > MaxSampleRadii) return Answer(world, "sample", false, "at most 16 radii");
        var c = site.Centre;
        var sb = new System.Text.StringBuilder(",\"sample\":{\"centreY\":").Append(G(c.Y));
        if (nearY is { } ny) sb.Append(",\"nearY\":").Append(G(ny));
        sb.Append(",\"radii\":[");
        for (int ri = 0; ri < words.Count; ri++)
        {
            if (!TryF(words[ri], out float r) || r < 0) return Answer(world, "sample", false, "radius needs a number >= 0");
            int walkC = 0, standC = 0, walkN = 0, standN = 0;
            var pts = new System.Text.StringBuilder();
            for (int k = 0; k < 36; k++)
            {
                float a = k * 10 * MathF.PI / 180f;
                float x = c.X + r * MathF.Cos(a), z = c.Z + r * MathF.Sin(a);
                bool w = PgEngine.IsWalkable(world, x, c.Y, z);
                bool st = PgEngine.TryStandAt(world, x, c.Y, z, out float y);
                if (w) walkC++;
                if (st) standC++;
                if (k > 0) pts.Append(',');
                pts.Append('[').Append((k * 10).ToString(CultureInfo.InvariantCulture)).Append(',').Append(w ? '1' : '0').Append(',')
                   .Append(st ? '1' : '0').Append(',').Append(Y1(st, y));
                if (nearY is { } nyy)
                {
                    bool wn = PgEngine.IsWalkable(world, x, nyy, z);
                    bool sn = PgEngine.TryStandAt(world, x, nyy, z, out float yn);
                    if (wn) walkN++;
                    if (sn) standN++;
                    pts.Append(',').Append(wn ? '1' : '0').Append(',').Append(sn ? '1' : '0').Append(',').Append(Y1(sn, yn));
                }
                pts.Append(']');
            }
            if (ri > 0) sb.Append(',');
            sb.Append("{\"r\":").Append(G(r)).Append(",\"walk\":").Append(walkC.ToString(CultureInfo.InvariantCulture))
              .Append(",\"stand\":").Append(standC.ToString(CultureInfo.InvariantCulture));
            if (nearY is not null)
                sb.Append(",\"walkNear\":").Append(walkN.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"standNear\":").Append(standN.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pts\":[").Append(pts).Append("]}");
        }
        sb.Append("]}");
        return Answer(world, "sample", true, null, sb.ToString());
    }

    const int MaxSampleRadii = 16;

    /// <summary>A sampled height, one decimal: <c>null</c> for a failed stand (its value is only the fallback) or a non-finite one (CMath's
    /// 1#IND / 1#INF are not JSON; as PgEngine.ProbeJson).</summary>
    static string Y1(bool stood, float v) => stood && float.IsFinite(v) ? CMath.FormatF(v, 1) : "null";

    static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);

    static bool TryKind(string s, out RingSiteKind kind)
    {
        kind = s switch { "arena" => RingSiteKind.Arena, "small" => RingSiteKind.SmallArena, "banner" => RingSiteKind.Banner, _ => (RingSiteKind)(-1) };
        return (int)kind >= 0;
    }

    static string G(double v) => TestJson.G(v);   // task 8.8: NaN -> null

    /// <summary>The PG-RING line: what was done, the site and radii in force, the counts, each participant's 2D distance from the centre.</summary>
    string Answer(World world, string verb, bool ok, string? error, string extra = "")
    {
        var t = BalanceTuning.Get();
        float sc = logic.RingScale;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"event\":\"ring\",\"verb\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(ok ? "true" : "false")
          .Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture));
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(extra);
        if (site is null) sb.Append(",\"site\":null");
        else
            sb.Append(",\"site\":{\"label\":").Append(PGLog.Quote(site.Label)).Append(",\"kind\":").Append(PGLog.Quote(site.Kind.ToString()))
              .Append(",\"x\":").Append(G(site.Centre.X)).Append(",\"y\":").Append(G(site.Centre.Y)).Append(",\"z\":").Append(G(site.Centre.Z)).Append('}');
        sb.Append(",\"scale\":").Append(G(sc))
          .Append(",\"near\":").Append(G((float)t.RingNearRadius * sc)).Append(",\"out\":").Append(G((float)t.RingOutRadius * sc))
          .Append(",\"return\":").Append(G((float)t.RingReturnRadius * sc))
          .Append(",\"returned\":").Append(logic.OutCorrections.ToString(CultureInfo.InvariantCulture))
          .Append(",\"steppedIn\":").Append(logic.NearCorrections.ToString(CultureInfo.InvariantCulture))
          .Append(",\"downedLeft\":").Append(logic.DownedSkips.ToString(CultureInfo.InvariantCulture))
          .Append(",\"unwalkableLeft\":").Append(logic.UnwalkableSkips.ToString(CultureInfo.InvariantCulture))
          .Append(",\"nearAt\":").Append(logic.NearAt.ToString(CultureInfo.InvariantCulture))
          .Append(",\"outAt\":").Append(logic.OutAt.ToString(CultureInfo.InvariantCulture))
          .Append(",\"now\":").Append(clock.NowMs.ToString(CultureInfo.InvariantCulture))
          .Append(",\"active\":").Append(spar.Running ? "true" : "false").Append(",\"fighters\":[");
        bool first = true;
        for (int i = 0; i < spar.GetParticipantCount(); i++)
        {
            if (spar.GetParticipant(i) is not { } c) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(PGLog.Quote(CharacterIdText.Format(c)));
            if (site is not null && PgEngine.Position(world, c) is { } p)
            {
                float dx = p.X - site.Centre.X, dz = p.Z - site.Centre.Z;
                sb.Append(",\"dist\":").Append(G(MathF.Sqrt(dx * dx + dz * dz)));
            }
            sb.Append(",\"eliminated\":").Append(spar.Running && spar.IsEliminated(world, c) ? "true" : "false")
              .Append(",\"downed\":").Append(Downed(world, c) ? "true" : "false").Append('}');
        }
        sb.Append("]}");
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-RING " + json);
        return json;
    }
}
