using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>The draw list from the snapshots, and markers for what has nothing to draw.</summary>
sealed partial class GameHost
{
    /// <summary>Tells the world where the camera looks, when it has moved enough to matter (the zones follow it).</summary>
    void SendFocus(Vector3? at = null)
    {
        if (IsDuel) return;   // the duel's worlds keep the focus on its spot (DuelSetup), wherever the camera flies
        var target = at ?? session.Camera.Current.Target;   // the cinema's camera while it holds the view
        if (sentFocus && Vector3.DistanceSquared(target, lastFocus) < 50 * 50) return;
        session.World.Commands.Enqueue(new Meitou.Simulation.FocusCommand(target) { Tick = session.World.Tick });
        lastFocus = target;
        sentFocus = true;
    }

    /// <summary>
    /// A layer's clip time between two ticks: from the last tick's time towards this one's, by the shorter way round the clip, so a looping clip wrapping (forwards, or backwards
    /// for the combat footwork clips whose play speed is negative) keeps moving through its end instead of holding or sweeping the whole clip in one tick. Without the clip's
    /// length (0) a time that went back is shown as it is.
    /// </summary>
    internal static float LayerTime(in Meitou.Simulation.AnimationLayer old, in Meitou.Simulation.AnimationLayer layer, float alpha)
    {
        float delta = layer.Time - old.Time, length = layer.Length;
        if (length > 0) { if (delta > length * 0.5f) delta -= length; else if (delta < -length * 0.5f) delta += length; }
        else if (delta < 0) return layer.Time;
        return old.Time + delta * alpha;
    }

    /// <summary>The facing a character is drawn at: its drawn body's, which follows the facing at a limited rate (<see cref="CharacterSnapshot.DrawYaw"/>).</summary>
    static float DrawnYaw(CharacterSnapshot c) => float.IsNaN(c.DrawYaw) ? c.Yaw : c.DrawYaw;

    void IndexPrevious()
    {
        var previous = session.PreviousSnapshot;
        if (ReferenceEquals(indexedFor, previous)) return;
        previousById.Clear();
        foreach (var c in previous.Characters) previousById[c.Id] = c;
        indexedFor = previous;
    }

    /// <summary>Where a character is drawn: between the last two snapshots, by the simulation's alpha (a carried body on its carrier's left shoulder).</summary>
    Vector3 DrawnPosition(CharacterSnapshot c)
    {
        IndexPrevious();
        var at = previousById.TryGetValue(c.Id, out var before) ? Vector3.Lerp(before.Position, c.Position, session.SimulationAlpha) : c.Position;
        return at + (c.Carry is { } carry ? MixCarry(before?.Carry, carry, session.SimulationAlpha).World(Vector3.Zero, c.Yaw).Position : Vector3.Zero);
    }

    /// <summary>The drawn facing of <paramref name="body"/>'s carrier between the last two snapshots (<paramref name="fallback"/> without it).</summary>
    float CarrierYaw(WorldSnapshot now, CharacterSnapshot body, float fallback, float alpha)
    {
        foreach (var k in now.Characters)
        {
            if (k.Id != body.CarriedBy) continue;
            return previousById.TryGetValue(k.Id, out var before) ? Meitou.Engine.Time.Interp.LerpAngle(DrawnYaw(before), DrawnYaw(k), alpha) : DrawnYaw(k);
        }
        return fallback;
    }

    /// <summary>A carried body's attach between two ticks (the carrier's pose moves on between them): offset and turn interpolated, so the body does not step at the tick rate.</summary>
    static Meitou.Simulation.Carrying.CarryAttach MixCarry(Meitou.Simulation.Carrying.CarryAttach? before, Meitou.Simulation.Carrying.CarryAttach now, float alpha) =>
        before is { } b ? new(Vector3.Lerp(b.Offset, now.Offset, alpha), Quaternion.Slerp(b.Rotation, now.Rotation, alpha)) : now;

    /// <summary>A carried body has its carrier's position; it is drawn at <see cref="CharacterSnapshot.Carry"/> from it, where its ragdoll is posed too.</summary>
    static Vector3 CarriedOffset(CharacterSnapshot c, float yaw) => c.Carry is { } carry ? carry.World(Vector3.Zero, yaw).Position : Vector3.Zero;

    /// <summary>Fills the renderer's list from the last two snapshots, interpolated by the simulation's alpha.</summary>
    void FillDrawList(CharacterDrawList list)
    {
        long f0 = Stopwatch.GetTimestamp();
        try { FillDrawListCore(list); FillPortraits(); }
        finally { fillTotal += Stopwatch.GetElapsedTime(f0).TotalMilliseconds; fillCalls++; }
    }

    /// <summary>
    /// The world's characters for a host without the player interface (the Metal loop): the same interpolation and ragdoll
    /// blending as <see cref="FillDrawListCore"/>, without the editor previews and the portraits.
    /// </summary>
    void FillWorldDrawList(CharacterDrawList list)
    {
        var current = session.CurrentSnapshot;
        IndexPrevious();
        float alpha = session.SimulationAlpha;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is null) continue;
            var position = c.Position;
            float yaw = DrawnYaw(c);
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                if (!Reanchored(found, c))
                {
                    position = Vector3.Lerp(found.Position, c.Position, alpha);
                    yaw = Meitou.Engine.Time.Interp.LerpAngle(DrawnYaw(found), DrawnYaw(c), alpha);
                }
            }
            Quaternion? rotation = null;
            if (c.Carry is { } carry)
            {
                yaw = CarrierYaw(current, c, yaw, alpha);
                (position, rotation) = MixCarry(before?.Carry, carry, alpha).World(position, yaw);
            }
            var poses = new CharacterPose[c.Animations.Count];
            for (int i = 0; i < poses.Length; i++)
            {
                var layer = c.Animations[i];
                float time = layer.Time, weight = layer.Weight;
                if (before is not null)
                    foreach (var old in before.Animations)
                        if (old.Name == layer.Name)
                        {
                            time = LayerTime(old, layer, alpha);
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            list.Add(new CharacterInstance(DrawKey(c.Id), c.Appearance, position, yaw, poses)
            {
                WeaponDrawn = c.WeaponDrawn, Scale = c.Scale, Hidden = hiddenIndoors.Contains(c.Id), FullRateAnimation = c.IsPlayer, Rotation = rotation,
                Bones = Blend(before is null || Reanchored(before, c) ? null : before.Ragdoll, c.Ragdoll, alpha, ref Slot(ref ragdollsBySlot, c.Id.Slot), ref Slot(ref spareRagdollsBySlot, c.Id.Slot)),
            });
        }
    }


    /// <summary>A character's key in the draw list: stable while it exists, new for every world (the renderer keeps its poses by it).</summary>
    long DrawKey(CharacterId id) => (((long)id.Slot << 32 | (uint)id.Generation) + 1) | (long)worldEpoch << 56;

    /// <summary>
    /// The portraits for the renderer to draw this frame: the squad bar's and the squads screen's members, the character trading window's rows and the conversation's two in
    /// their cells (docs/character-renderer.md "Portraits").
    /// </summary>
    void FillPortraits()
    {
        if (gpu.Characters is not { } characters) return;
        characters.PortraitRequests.Clear();
        if (player.TitleOpen) return;
        // The serial turns each portrait's camera.
        if (player.UiVisible)
        {
            var members = player.PortraitMembers();
            for (int i = 0; i < members.Count; i++)
                characters.PortraitRequests.Add(new PortraitRequest(DrawKey(members[i].Id), i, members[i].PortraitSerial));
            foreach (var (id, cell) in player.CharacterTradePortraits())
                characters.PortraitRequests.Add(new PortraitRequest(DrawKey(id), cell, player.PortraitSerial(id)));
        }
        if (player.DialoguePortraitCharacters() is var (npc, talker))
        {
            if (!npc.IsNone) characters.PortraitRequests.Add(new PortraitRequest(DrawKey(npc), PlayerInterface.DialogueNpcCell, player.PortraitSerial(npc)));
            if (!talker.IsNone) characters.PortraitRequests.Add(new PortraitRequest(DrawKey(talker), PlayerInterface.DialoguePlayerCell, player.PortraitSerial(talker)));
        }
    }

    // The editor's stances and their weights, of the character last shown, and when they were last stepped (EditorStancePoses).
    readonly List<(string Stance, float Weight)> editorStances = [];
    CharacterId editorStancesOf;
    double editorStancesAt;

    /// <summary>
    /// The edited character's poses: the chosen idle stance at its record's play speed, as the simulation plays it (0 for the stances: a held pose of a 1-frame clip;
    /// 0.01 for squat), cross-faded from the stances chosen before at the simulation's blend rate (<c>animation blend rate</c>).
    /// </summary>
    CharacterPose[] EditorStancePoses(CharacterId id, string stance)
    {
        double now = realTime.Elapsed.TotalSeconds;
        if (id != editorStancesOf || editorStances.Count == 0) { editorStances.Clear(); editorStances.Add((stance, 1)); editorStancesOf = id; }
        else if (!editorStances.Exists(s => s.Stance == stance)) editorStances.Add((stance, 0));
        float step = (float)(now - editorStancesAt) * (popData?.Bodies.Constants.AnimationBlendRate ?? 4);
        editorStancesAt = now;
        for (int i = editorStances.Count - 1; i >= 0; i--)
        {
            var (name, weight) = editorStances[i];
            weight = name == stance ? MathF.Min(weight + step, 1) : weight - step;
            if (weight <= 0) editorStances.RemoveAt(i);
            else editorStances[i] = (name, weight);
        }
        return [.. editorStances.Select(s => new CharacterPose(s.Stance, animationLibrary?.IndexOf(s.Stance) is >= 0 and int k ? (float)now * animationLibrary.Definitions[k].PlaySpeed : 0, s.Weight))];
    }

    readonly HashSet<CharacterId> fullRate = [];
    // By character slot, what the draw list hands the renderer, refilled every frame instead of made anew (the renderer reads them only in that frame's update):
    // the poses, the stuck bolts and the ragdoll between ticks.
    CharacterPose[]?[] posesBySlot = [];
    StuckBoltMesh[]?[] boltsBySlot = [];
    // The ragdoll's two alternate, so a blended one is a new object to the renderer each frame (it poses a ragdoll again only for other bones).
    Meitou.Data.Characters.BonePose?[] ragdollsBySlot = [], spareRagdollsBySlot = [];

    /// <summary><paramref name="slots"/>[<paramref name="slot"/>], the array grown to hold it.</summary>
    static ref T? Slot<T>(ref T?[] slots, int slot) where T : class
    {
        if (slot >= slots.Length) Array.Resize(ref slots, Math.Max(slot + 1, slots.Length * 2));
        return ref slots[slot];
    }

    void FillDrawListCore(CharacterDrawList list)
    {
        var current = session.CurrentSnapshot;
        IndexPrevious();
        float alpha = session.SimulationAlpha;
        // The character editor is a scene of its own (GameHost.Editor.cs): only the character being edited is posed and drawn.
        var editing = player.EditorFocus?.Character;
        player.FullRateCharacters(fullRate);
        foreach (var c in current.Characters)
        {
            var inBar = editing is { } shown ? EditorBarPlace(c.Id, shown) : null;
            if (c.Appearance is null || editing is { } edited && c.Id != edited && inBar is null) continue;
            var position = c.Position;
            float yaw = DrawnYaw(c);
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                if (!Reanchored(found, c))
                {
                    position = Vector3.Lerp(found.Position, c.Position, alpha);
                    yaw = Meitou.Engine.Time.Interp.LerpAngle(DrawnYaw(found), DrawnYaw(c), alpha);
                }
            }
            Quaternion? rotation = null;
            if (c.Carry is { } carry)
            {
                // The body hangs on its carrier's drawn shoulder: the carrier's drawn facing (it turns at a limited rate), not the simulation's.
                yaw = CarrierYaw(current, c, yaw, alpha);
                (position, rotation) = MixCarry(before?.Carry, carry, alpha).World(position, yaw);
            }
            var poses = Slot(ref posesBySlot, c.Id.Slot) is { } kept && kept.Length == c.Animations.Count ? kept : posesBySlot[c.Id.Slot] = new CharacterPose[c.Animations.Count];
            for (int i = 0; i < poses.Length; i++)
            {
                var layer = c.Animations[i];
                float time = layer.Time, weight = layer.Weight;
                // Between ticks the clip time and weight move on from the last snapshot (a wrap of a looping clip goes through the wrap: LayerTime).
                if (before is not null)
                    for (int k = 0; k < before.Animations.Count; k++)   // indexed: a foreach over the list's interface boxed an enumerator per layer per frame
                        if (before.Animations[k] is var old && old.Name == layer.Name)
                        {
                            time = LayerTime(old, layer, alpha);
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            if (player.EditorIdle(c.Id) is { } stance) poses = EditorStancePoses(c.Id, stance);
            // The Meitou editor's bar (GameHost.EditorBar.cs): the shown character stands in it, the others sit on its seats.
            if (inBar is { } place)
            {
                (position, yaw, rotation) = (place.Position, place.Yaw, null);
                if (place.Clip is { } clip)
                    poses = [new CharacterPose(clip, animationLibrary?.IndexOf(clip) is >= 0 and int k ? (float)realTime.Elapsed.TotalSeconds * animationLibrary.Definitions[k].PlaySpeed : 0, 1)];
            }
            if (c.Id == sandboxHero && sandboxPicker?.Pose() is { } forced) poses = forced;   // the sandbox drop-down loops a picked clip on the hero
            var held = HeldLook(c.Appearance, c.HeldTool, out var heldPending);
            var look = player.EditorPreview(c.Id, c.Appearance) ?? held;
            if (editing is null) CutHead(c, look);
            var glide = player.EditorBlend(c.Id);   // after the preview, which may just have swapped the look
            list.Add(new CharacterInstance(DrawKey(c.Id), look, position, yaw, poses)
            {
                WeaponDrawn = c.WeaponDrawn, RangedDrawn = c.RangedDrawn, BackDrawn = c.BackDrawn, Shots = c.CrossbowShots, Bolts = BoltsOf(c), Scale = c.Scale, Hidden = inBar is null && hiddenIndoors.Contains(c.Id), FullRateAnimation = editing is not null || fullRate.Contains(c.Id) || c.Id == sandboxHero && sandboxPicker?.Current is not null,
                Bones = Blend(before is null || Reanchored(before, c) ? null : before.Ragdoll, c.Ragdoll, alpha, ref Slot(ref ragdollsBySlot, c.Id.Slot), ref Slot(ref spareRagdollsBySlot, c.Id.Slot)),
                BlendFrom = glide?.From, Blend = glide?.Blend ?? 0, Build = c.Build, Rotation = rotation, Blood = c.BloodAmounts,
            });
            // The editor's next preview look, listed hidden so the renderer prepares it while the last one is still shown.
            if (player.EditorPending(c.Id) is { } pending)
                list.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), pending, position, yaw, poses) { Hidden = true });
            else if (heldPending is not null)
                list.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), heldPending, position, yaw, poses) { Hidden = true });
        }
        if (editing is null) AddHeads(list);
        else AddEditorCrowd(list);
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Meitou.Data.Characters.CharacterAppearance, Dictionary<string, Meitou.Data.Characters.CharacterAppearance>> heldLooks = new();

    /// <summary>
    /// The look with the tool an operator holds (<see cref="CharacterSnapshot.HeldTool"/>), made once per look and tool. Until the renderer has prepared it the plain look is
    /// drawn and the tool's look is <paramref name="pending"/>, to be listed hidden so it gets prepared.
    /// </summary>
    Meitou.Data.Characters.CharacterAppearance HeldLook(Meitou.Data.Characters.CharacterAppearance look, string? tool, out Meitou.Data.Characters.CharacterAppearance? pending)
    {
        pending = null;
        if (tool is null || scene.Database is not { } db) return look;
        var byTool = heldLooks.GetOrCreateValue(look);
        if (!byTool.TryGetValue(tool, out var held)) byTool[tool] = held = db.Find(tool) is { } item ? look.WithHeld(item) : look;
        if (gpu.Characters is not { } characters || characters.IsPrepared(held)) return held;
        pending = held;
        return look;
    }

    /// <summary>
    /// Whether the character's place changed meaning between the two ticks: a whole ragdoll moves it to its root body the tick it starts and back onto the ground the tick
    /// it gets up (docs/game/ragdolls.md "Per frame"), and its pose is placed there. Such a tick is drawn where it ends, not between: from the last place, the body sank by its
    /// hip height into the ground for a frame (or hopped up as it rose).
    /// </summary>
    static bool Reanchored(CharacterSnapshot before, CharacterSnapshot now) =>
        (before.Ragdoll is null) != (now.Ragdoll is null) || before.Ragdoll is { Weight: >= 1 } && now.Ragdoll is { Weight: < 1 };

    /// <summary>
    /// A ragdoll's bones between the last two ticks: rotations by nlerp, root positions and the get-up weight linearly; a bone only in the newer one as it is. Written into
    /// <paramref name="kept"/> or <paramref name="spare"/>, the other than last time (made when missing or of another size). A frozen ragdoll's (the same bones in both) as it is.
    /// </summary>
    static Meitou.Data.Characters.BonePose? Blend(Meitou.Data.Characters.BonePose? from, Meitou.Data.Characters.BonePose? to, float alpha, ref Meitou.Data.Characters.BonePose? kept,
        ref Meitou.Data.Characters.BonePose? spare)
    {
        if (to is null || from is null || ReferenceEquals(from, to) || from.Set.Length != to.Set.Length) return to;
        (kept, spare) = (spare, kept);
        if (kept is null || kept.Set.Length != to.Set.Length) kept = new Meitou.Data.Characters.BonePose(to.Set.Length);
        var pose = kept;
        pose.Weight = from.Weight + (to.Weight - from.Weight) * alpha;
        for (int h = 0; h < to.Set.Length; h++)
        {
            pose.Set[h] = to.Set[h];
            if (!to.Set[h]) { (pose.Rotation[h], pose.Position[h]) = (default, default); continue; }
            if (!from.Set[h]) { (pose.Rotation[h], pose.Position[h]) = (to.Rotation[h], to.Position[h]); continue; }
            var b = to.Rotation[h];
            if (Quaternion.Dot(from.Rotation[h], b) < 0) b = -b;
            pose.Rotation[h] = Quaternion.Normalize(Quaternion.Lerp(from.Rotation[h], b, alpha));
            pose.Position[h] = Vector3.Lerp(from.Position[h], to.Position[h], alpha);
        }
        return pose;
    }

    /// <summary>Squares over the characters that have no drawing (a failed build, a world without looks), coloured by faction.</summary>
    void DrawMarkers(DebugOverlay overlay, int width, int height)
    {
        var view = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        var current = session.CurrentSnapshot;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is not null) continue;
            var clip = Vector4.Transform(new Vector4(c.Position + new Vector3(0, 10, 0), 1), view);
            if (clip.W <= 0.1f) continue;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            if (x < -10 || y < -10 || x > width + 10 || y > height + 10) continue;
            float hue = c.Faction < 0 ? 0 : (c.Faction * 0.618034f) % 1f;
            var colour = new Vector4(0.5f + 0.5f * MathF.Sin(hue * MathF.Tau), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 2.1f), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 4.2f), 1);
            overlay.Rect(x - 3, y - 3, x + 3, y + 3, colour);
        }
        overlay.Flush(width, height);
    }
}
