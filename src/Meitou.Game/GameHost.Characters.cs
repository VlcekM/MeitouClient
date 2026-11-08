using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Settings;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Session;
using Meitou.Game.Rendering.Characters;
using Meitou.Simulation;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Game.Host.Input.Key;
using EngineButton = Meitou.Game.Host.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>The draw list from the snapshots, and markers for what has nothing to draw.</summary>
sealed partial class GameHost
{
    /// <summary>The free camera's eye this far above the ground under it keeps the last focus: the zones load again once it comes down.</summary>
    const float FocusCeiling = Meitou.Data.World.KenshiCamera.FlightHeight;

    /// <summary>The window's aspect ratio, kept by the frame loop for <see cref="SendView"/>.</summary>
    float viewAspect = 16f / 9;

    /// <summary>The settings' <c>npc range</c>, read once (the drawing takes it once too; reading settings.cfg every frame cost 0.09 ms).</summary>
    float? npcRange;

    /// <summary>Tells the world the camera it is looked at through, every frame (the update flag of <see cref="Meitou.Simulation.Bodies.MedicalTimeBudget"/> and the sensing hooks, <see cref="Meitou.Simulation.Bodies.MedicalClock"/>).</summary>
    void SendView()
    {
        if (IsBattle) return;
        session.World.Commands.Enqueue(new Meitou.Simulation.Bodies.CameraViewCommand(camera.Eye, camera.Forward, camera.FieldOfView, viewAspect, npcRange ??= GameplaySettings.NpcRange(install)) { Tick = session.World.Tick });
    }

    /// <summary>Tells the world where the camera looks, when it has moved enough to matter (the zones follow it).</summary>
    void SendFocus(Vector3? at = null)
    {
        if (IsDuel) return;   // the duel's worlds keep the focus on its spot (DuelSetup), wherever the camera flies
        SendView();
        var current = session.Camera.Current;
        if (at is null && sentFocus && current.IsFree && current.Eye.Y - session.Camera.EyeGroundAt(current.Eye.X, current.Eye.Z) > FocusCeiling) return;
        var target = at ?? current.Target;   // the cinema's camera while it holds the view
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

    internal void IndexPrevious()
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
            return previousById.TryGetValue(k.Id, out var before) ? Meitou.Simulation.Session.Interp.LerpAngle(DrawnYaw(before), DrawnYaw(k), alpha) : DrawnYaw(k);
        }
        return fallback;
    }

    /// <summary>A carried body's attach between two ticks (the carrier's pose moves on between them): offset and turn interpolated, so the body does not step at the tick rate.</summary>
    static Meitou.Simulation.Carrying.CarryAttach MixCarry(Meitou.Simulation.Carrying.CarryAttach? before, Meitou.Simulation.Carrying.CarryAttach now, float alpha) =>
        before is { } b ? new(Vector3.Lerp(b.Offset, now.Offset, alpha), Quaternion.Slerp(b.Rotation, now.Rotation, alpha)) : now;

    /// <summary>A carried body has its carrier's position; it is drawn at <see cref="CharacterSnapshot.Carry"/> from it, where its ragdoll is posed too.</summary>
    internal static Vector3 CarriedOffset(CharacterSnapshot c, float yaw) => c.Carry is { } carry ? carry.World(Vector3.Zero, yaw).Position : Vector3.Zero;

    /// <summary>Fills the renderer's list from the last two snapshots, interpolated by the simulation's alpha.</summary>
    void FillDrawList(CharacterDrawList list)
    {
        long f0 = Stopwatch.GetTimestamp();
        // The portraits first: their characters are posed however far (FillDrawListCore).
        try { FillPortraits(); FillDrawListCore(list); }
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
                    yaw = Meitou.Simulation.Session.Interp.LerpAngle(DrawnYaw(found), DrawnYaw(c), alpha);
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
                    for (int k = 0; k < before.Animations.Count; k++)   // indexed: a foreach over the interface boxes its enumerator
                        if (before.Animations[k] is var old && old.Name == layer.Name)
                        {
                            time = LayerTime(old, layer, alpha);
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            var bones = Blend(before is null || Reanchored(before, c) ? null : before.Ragdoll, c.Ragdoll, alpha, ref Slot(ref ragdollsBySlot, c.Id.Slot), ref Slot(ref spareRagdollsBySlot, c.Id.Slot));
            list.Add(Drawn(c, c.Appearance, position, yaw, poses, rotation, bones, hiddenIndoors.Contains(c.Id), c.IsPlayer));
        }
    }


    /// <summary>
    /// A character as the renderer is handed it, by both draw-list sources (<see cref="FillDrawListCore"/>, the interface-less <see cref="FillWorldDrawList"/> of the Metal loop):
    /// everything the snapshot says of its look, so a field is added here once (the stuck bolts, the limb in an animal's mouth, the weapon stance, the build, the blood).
    /// </summary>
    CharacterInstance Drawn(CharacterSnapshot c, Meitou.Data.Characters.CharacterAppearance look, Vector3 position, float yaw, IReadOnlyList<CharacterPose> poses, Quaternion? rotation,
        Meitou.Data.Characters.BonePose? bones, bool hidden, bool fullRate) =>
        new(DrawKey(c.Id), look, position, yaw, poses)
        {
            WeaponDrawn = c.WeaponDrawn, RangedDrawn = c.RangedDrawn, BackDrawn = c.BackDrawn, Shots = c.CrossbowShots, Bolts = debris.BoltsOf(c), Mouth = debris.MouthOf(c),
            Scale = c.Scale, DrawDistanceScale = c.DrawDistanceScale, Hidden = hidden, FullRateAnimation = fullRate, Bones = bones,
            Build = c.Build, Rotation = rotation, Blood = c.BloodAmounts,
        };

    /// <summary>A character's key in the draw list: stable while it exists, new for every world (the renderer keeps its poses by it).</summary>
    internal long DrawKey(CharacterId id) => (((long)id.Slot << 32 | (uint)id.Generation) + 1) | (long)worldEpoch << 56;

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
    /// 0.01 for squat; a mod's one-shot stance held at its last frame, <see cref="EditorClipTime"/>), cross-faded from the stances chosen before at the simulation's blend rate
    /// (<c>animation blend rate</c>).
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
        return [.. editorStances.Select(s => new CharacterPose(s.Stance, EditorClipTime(s.Stance, (float)now), s.Weight))];
    }

    readonly HashSet<CharacterId> fullRate = [];
    readonly HashSet<long> portraitKeys = [];
    /// <summary>The snapshot the distant list was made from (<see cref="AddDistant"/>), its tick and the world.</summary>
    (WorldSnapshot?, long, int) distantFor;

    /// <summary>A character beyond reach into the distant list: its look (with its tool), its snapshot place, and what loading and the kept textures read.</summary>
    void AddDistant(CharacterDrawList list, CharacterSnapshot c)
    {
        var look = HeldLook(c.Appearance!, c.HeldTool, out var heldPending);
        decapitation.CutHead(c, look);
        bool hidden = hiddenIndoors.Contains(c.Id);
        list.Distant.Add(new CharacterInstance(DrawKey(c.Id), look, c.Position, DrawnYaw(c), []) { Scale = c.Scale, Hidden = hidden, Build = c.Build });
        if (heldPending is not null) list.Distant.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), heldPending, c.Position, DrawnYaw(c), []) { Hidden = true });
    }
    // By character slot, what the draw list hands the renderer, refilled every frame instead of made anew (the renderer reads them only in that frame's update):
    // the poses, the stuck bolts and the ragdoll between ticks.
    CharacterPose[]?[] posesBySlot = [];
    internal StuckBoltMesh[]?[] boltsBySlot = [];
    // The ragdoll's two alternate, so a blended one is a new object to the renderer each frame (it poses a ragdoll again only for other bones).
    Meitou.Data.Characters.BonePose?[] ragdollsBySlot = [], spareRagdollsBySlot = [];

    /// <summary><paramref name="slots"/>[<paramref name="slot"/>], the array grown to hold it.</summary>
    internal static ref T? Slot<T>(ref T?[] slots, int slot) where T : class
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
        // Beyond the renderer's reach a character needs its look and place only (to load it), not its poses or ragdoll; those go in the distant list, made again only
        // when the snapshot changes (CharacterDrawList.Distant). The editor's scene is listed whole every frame.
        var reachOf = editing is null && !player.EditorOpen ? gpu.Characters : null;
        var eye = camera.Eye;
        portraitKeys.Clear();
        if (reachOf is not null) foreach (var r in reachOf.PortraitRequests) portraitKeys.Add(r.Key);
        var distantKey = (current, current.Tick, worldEpoch);
        bool distant = reachOf is not null && distantKey != distantFor;
        if (reachOf is null ? list.Distant.Count > 0 : distant) list.BeginDistant();
        distantFor = reachOf is null ? default : distantKey;
        foreach (var c in current.Characters)
        {
            var inBar = editing is { } shown ? EditorBarPlace(c.Id, shown) : null;
            if (c.Appearance is null || editing is { } edited && c.Id != edited && inBar is null) continue;
            // The snapshot's place: a tick's motion is well inside InReach's margin.
            if (reachOf is not null && !reachOf.InReach(c.Position, eye, c.DrawDistanceScale) && !portraitKeys.Contains(DrawKey(c.Id)) && c.Id != sandboxHero)
            {
                if (distant) AddDistant(list, c);
                continue;
            }
            var position = c.Position;
            float yaw = DrawnYaw(c);
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                if (!Reanchored(found, c))
                {
                    position = Vector3.Lerp(found.Position, c.Position, alpha);
                    yaw = Meitou.Simulation.Session.Interp.LerpAngle(DrawnYaw(found), DrawnYaw(c), alpha);
                }
            }
            Quaternion? rotation = null;
            if (c.Carry is { } carry)
            {
                // The body hangs on its carrier's drawn shoulder: the carrier's drawn facing (it turns at a limited rate), not the simulation's.
                yaw = CarrierYaw(current, c, yaw, alpha);
                (position, rotation) = MixCarry(before?.Carry, carry, alpha).World(position, yaw);
            }
            // A character hidden indoors is not drawn (CharacterRenderer: as one out of reach) unless for its portrait: its poses are not read either.
            bool posed = reachOf is null || !hiddenIndoors.Contains(c.Id) || portraitKeys.Contains(DrawKey(c.Id));
            var poses = !posed ? [] : Slot(ref posesBySlot, c.Id.Slot) is { } kept && kept.Length == c.Animations.Count ? kept : posesBySlot[c.Id.Slot] = new CharacterPose[c.Animations.Count];
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
            // An animal in the editor plays its own idle in real time (the world may stand paused behind the editor).
            else if (player.IsEditorAnimal(c.Id)) poses = [.. poses.Select(p => p with { Time = EditorClipTime(p.Animation, (float)realTime.Elapsed.TotalSeconds) })];
            // The Meitou editor's bar (GameHost.EditorBar.cs): the shown character stands in it, the others sit on its seats.
            if (inBar is { } place)
            {
                (position, yaw, rotation) = (place.Position, place.Yaw, null);
                if (place.Clip is { } clip) poses = [new CharacterPose(clip, EditorClipTime(clip, (float)realTime.Elapsed.TotalSeconds), 1)];
            }
            if (c.Id == sandboxHero && sandboxPicker?.Pose() is { } forced) poses = forced;   // the sandbox drop-down loops a picked clip on the hero
            var held = HeldLook(c.Appearance, c.HeldTool, out var heldPending);
            var look = player.EditorPreview(c.Id, c.Appearance) ?? held;
            if (editing is null) decapitation.CutHead(c, look);
            var glide = player.EditorBlend(c.Id);   // after the preview, which may just have swapped the look
            var bones = !posed ? null : Blend(before is null || Reanchored(before, c) ? null : before.Ragdoll, c.Ragdoll, alpha, ref Slot(ref ragdollsBySlot, c.Id.Slot), ref Slot(ref spareRagdollsBySlot, c.Id.Slot));
            bool hidden = inBar is null && hiddenIndoors.Contains(c.Id);
            bool fullRated = editing is not null || fullRate.Contains(c.Id) || c.Id == sandboxHero && sandboxPicker?.Current is not null;
            var instance = Drawn(c, look, position, yaw, poses, rotation, bones, hidden, fullRated);
            if (glide is { } mixed) instance = instance with { BlendFrom = mixed.From, Blend = mixed.Blend };
            list.Add(instance);
            // The editor's next preview look, listed hidden so the renderer prepares it while the last one is still shown.
            if (player.EditorPending(c.Id) is { } pending)
                list.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), pending, position, yaw, poses) { Hidden = true });
            else if (heldPending is not null)
                list.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), heldPending, position, yaw, poses) { Hidden = true });
        }
        if (editing is null) decapitation.AddHeads(list);
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

    /// <summary>Whether the look with the operator's tool (<see cref="HeldLook"/>) is made and prepared: then it is the one drawn.</summary>
    bool HeldPrepared(Meitou.Data.Characters.CharacterAppearance look, string? tool, Meitou.Game.Rendering.Characters.CharacterRenderer characters) =>
        tool is not null && heldLooks.TryGetValue(look, out var byTool) && byTool.TryGetValue(tool, out var held) && characters.IsPrepared(held);

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
