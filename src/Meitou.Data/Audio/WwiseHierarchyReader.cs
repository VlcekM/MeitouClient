using System.Buffers.Binary;

namespace Meitou.Data.Audio;

/// <summary>
/// Decodes a version 112 <c>HIRC</c> section (docs/formats/wwise-bnk.md#hirc). Each object is u8 type, u32 size, then <c>size</c> bytes
/// starting with its u32 id. A decoded object must use exactly its size; one that does not, or a type that is not decoded, is kept as
/// <see cref="WwiseRawObject"/> (with the error when decoding failed).
/// </summary>
static class WwiseHierarchyReader
{
    /// <summary>
    /// Splits the section into its objects, decoded when <paramref name="decode"/>, else all kept raw; <see cref="InvalidDataException"/> when an
    /// object runs past the section or bytes are left over.
    /// </summary>
    public static List<WwiseObject> Read(ReadOnlySpan<byte> hirc, bool decode)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(hirc);
        var objects = new List<WwiseObject>(count);
        int p = 4;
        for (int i = 0; i < count; i++)
        {
            if (p + 9 > hirc.Length) throw new InvalidDataException($"HIRC object {i} header runs past the section");
            var type = (WwiseObjectType)hirc[p];
            int size = BinaryPrimitives.ReadInt32LittleEndian(hirc[(p + 1)..]);
            if (size < 4 || p + 5 + (long)size > hirc.Length)
                throw new InvalidDataException($"HIRC object {i} runs past the section");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hirc[(p + 5)..]);
            var body = hirc.Slice(p + 9, size - 4);
            objects.Add(decode ? Decode(type, id, body) : new WwiseRawObject(type, id, body.ToArray(), null));
            p += 5 + size;
        }
        if (p != hirc.Length) throw new InvalidDataException("HIRC has trailing bytes");
        return objects;
    }

    /// <summary>
    /// Decodes one object's <paramref name="body"/> (after its id) by its type; a type not decoded, a layout error or bytes left over keep it raw.
    /// </summary>
    static WwiseObject Decode(WwiseObjectType type, uint id, ReadOnlySpan<byte> body)
    {
        if (type is WwiseObjectType.Bus or WwiseObjectType.AuxBus) return Bus(id, body, type == WwiseObjectType.AuxBus);
        var r = new Reader(body);
        try
        {
            WwiseObject? o = type switch
            {
                WwiseObjectType.Event => new WwiseEvent(id, r.Ids(r.U32())),
                WwiseObjectType.Action => Action(ref r, id),
                WwiseObjectType.Sound => new WwiseSound(id, Source(ref r), NodeBase(ref r)),
                WwiseObjectType.ActorMixer => new WwiseActorMixer(id, NodeBase(ref r), r.Ids(r.U32())),
                WwiseObjectType.RandomSequenceContainer => RandomSequence(ref r, id),
                WwiseObjectType.SwitchContainer => Switch(ref r, id),
                WwiseObjectType.LayerContainer => Layer(ref r, id),
                WwiseObjectType.MusicSegment => MusicSegment(ref r, id),
                WwiseObjectType.MusicTrack => MusicTrack(ref r, id),
                WwiseObjectType.MusicSwitch => MusicSwitch(ref r, id),
                WwiseObjectType.MusicRandomSequence => MusicPlaylist(ref r, id),
                WwiseObjectType.Attenuation => Attenuation(ref r, id),
                WwiseObjectType.FxShareSet or WwiseObjectType.FxCustom => Effect(ref r, type, id),
                _ => null,
            };
            if (o is null) return new WwiseRawObject(type, id, body.ToArray(), null);
            if (r.Remaining != 0) return new WwiseRawObject(type, id, body.ToArray(), $"{r.Remaining} bytes left over");
            return o;
        }
        catch (InvalidDataException e)
        {
            return new WwiseRawObject(type, id, body.ToArray(), e.Message);
        }
    }

    /// <summary>
    /// An action: type, target, target-is-bus, properties, then the fields of its kind (docs/formats/wwise-bnk.md "Action").
    /// </summary>
    static WwiseAction Action(ref Reader r, uint id)
    {
        ushort actionType = r.U16();
        uint target = r.U32();
        bool isBus = r.U8() != 0;
        var props = Properties(ref r);
        var action = new WwiseAction(id, actionType, target, isBus, props);
        switch ((WwiseActionKind)(actionType >> 8))
        {
            case WwiseActionKind.Play:
                return action with { FadeCurve = r.U8(), BankId = r.U32() };
            case WwiseActionKind.Stop or WwiseActionKind.Pause or WwiseActionKind.Resume:
                return action with { FadeCurve = r.U8(), Exceptions = Exceptions(ref r) };
            case WwiseActionKind.SetPitch or WwiseActionKind.ResetPitch or WwiseActionKind.SetVolume or WwiseActionKind.ResetVolume
                or WwiseActionKind.SetBusVolume or WwiseActionKind.ResetBusVolume or WwiseActionKind.SetLowPass or WwiseActionKind.ResetLowPass:
                return action with
                {
                    FadeCurve = r.U8(),
                    ValueMeaning = r.U8(),
                    Value = r.F32(),
                    ValueMin = r.F32(),
                    ValueMax = r.F32(),
                    Exceptions = Exceptions(ref r),
                };
            case WwiseActionKind.SetState:
                return action with { GroupId = r.U32(), StateId = r.U32() };
            case WwiseActionKind.BypassFx:
                return action with { Bypass = r.U8() != 0, BypassMask = r.U8(), Exceptions = Exceptions(ref r) };
            default:
                throw new InvalidDataException($"Action type {actionType:X4} not decoded");
        }
    }

    /// <summary>
    /// An action's exception list: u32 count, then per entry a u32 id and a u8 is-bus flag.
    /// </summary>
    static (uint, bool)[] Exceptions(ref Reader r)
    {
        var list = new (uint, bool)[r.Count(r.U32(), 5)];
        for (int i = 0; i < list.Length; i++) list[i] = (r.U32(), r.U8() != 0);
        return list;
    }

    /// <summary>
    /// u8 count, the ids, the u32 values; then u8 count, the ids, the (min, max) float pairs.
    /// </summary>
    static WwiseProperties Properties(ref Reader r)
    {
        int n = r.U8();
        var ids = r.Bytes(n);
        var values = new Dictionary<byte, uint>(n);
        for (int i = 0; i < n; i++) values[ids[i]] = r.U32();
        int m = r.U8();
        var rangeIds = r.Bytes(m);
        var ranges = new Dictionary<byte, (float, float)>(m);
        for (int i = 0; i < m; i++) ranges[rangeIds[i]] = (r.F32(), r.F32());
        return n == 0 && m == 0 ? WwiseProperties.Empty : new WwiseProperties(values, ranges);
    }

    /// <summary>
    /// A sound or track source: plugin, stream type, source and file ids; an embedded one (stream type 0) adds its offset and size in the bank.
    /// </summary>
    static WwiseSource Source(ref Reader r)
    {
        uint plugin = r.U32();
        byte stream = r.U8();
        uint source = r.U32(), file = r.U32();
        uint offset = 0, size = 0;
        if (stream == 0)
        {
            offset = r.U32();
            size = r.U32();
        }
        else throw new InvalidDataException($"Stream type {stream} not decoded");
        byte bits = r.U8();
        byte[] parameters = [];
        if ((plugin & 0xF) == 2) parameters = r.Bytes(r.Count(r.U32(), 1));
        return new WwiseSource(plugin, stream, source, file, offset, size, bits, parameters);
    }

    /// <summary>
    /// The parameters every sound and container starts with (docs/formats/wwise-bnk.md "Node base").
    /// </summary>
    static WwiseNodeBase NodeBase(ref Reader r)
    {
        bool overrideFx = r.U8() != 0;
        int fxCount = r.U8();
        byte bypass = 0;
        var fx = new (byte, uint, bool, bool)[fxCount];
        if (fxCount > 0)
        {
            bypass = r.U8();
            for (int i = 0; i < fxCount; i++) fx[i] = (r.U8(), r.U32(), r.U8() != 0, r.U8() != 0);
        }
        byte unknown = r.U8();
        uint bus = r.U32(), parent = r.U32();
        byte priority = r.U8();
        var props = Properties(ref r);
        var positioning = Positioning(ref r);
        byte auxBits = r.U8();
        uint[] aux = (auxBits & 8) != 0 ? r.Ids(4) : [];
        byte adv = r.U8(), virtualQueue = r.U8();
        ushort maxInstances = r.U16();
        byte below = r.U8(), adv2 = r.U8();
        var states = new WwiseStateGroup[r.Count(r.U32(), 7)];
        for (int i = 0; i < states.Length; i++)
        {
            uint group = r.U32();
            byte sync = r.U8();
            var list = new (uint, uint)[r.Count(r.U16(), 8)];
            for (int j = 0; j < list.Length; j++) list[j] = (r.U32(), r.U32());
            states[i] = new WwiseStateGroup(group, sync, list);
        }
        return new WwiseNodeBase(overrideFx, fx, bypass, unknown, bus, parent, priority, props, positioning, auxBits, aux, adv, virtualQueue,
            maxInstances, below, adv2, states, Rtpcs(ref r));
    }

    /// <summary>
    /// A node's positioning: its bits, and when it overrides its parent's and is 3D, the 3D settings and any paths.
    /// </summary>
    static WwisePositioning Positioning(ref Reader r)
    {
        byte bits = r.U8();
        byte bits3D = 0, pathMode = 0;
        uint attenuation = 0;
        int transition = 0;
        WwisePathVertex[] vertices = [];
        (uint, uint)[] paths = [];
        (float, float, float)[] ranges = [];
        if ((bits & 1) != 0 && (bits & 8) != 0)
        {
            bits3D = r.U8();
            attenuation = r.U32();
            if (((bits >> 5) & 3) == 3)
            {
                pathMode = r.U8();
                transition = r.I32();
                vertices = new WwisePathVertex[r.Count(r.U32(), 16)];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = new WwisePathVertex(r.F32(), r.F32(), r.F32(), r.I32());
                paths = new (uint, uint)[r.Count(r.U32(), 20)];
                for (int i = 0; i < paths.Length; i++) paths[i] = (r.U32(), r.U32());
                ranges = new (float, float, float)[paths.Length];
                for (int i = 0; i < ranges.Length; i++) ranges[i] = (r.F32(), r.F32(), r.F32());
            }
        }
        return new WwisePositioning(bits, bits3D, attenuation, pathMode, transition, vertices, paths, ranges);
    }

    /// <summary>
    /// An RTPC list: u16 count, then per RTPC its parameter, type, accumulation, target property, curve id, scaling and points.
    /// </summary>
    static WwiseRtpc[] Rtpcs(ref Reader r)
    {
        var list = new WwiseRtpc[r.Count(r.U16(), 14)];
        for (int i = 0; i < list.Length; i++)
        {
            uint parameter = r.U32();
            byte type = r.U8(), accumulation = r.U8(), param = r.U8();
            uint curve = r.U32();
            byte scaling = r.U8();
            list[i] = new WwiseRtpc(parameter, type, accumulation, param, curve, scaling, Points(ref r, r.U16()));
        }
        return list;
    }

    /// <summary>
    /// <paramref name="count"/> curve points of (f32 x, f32 y, u32 interpolation).
    /// </summary>
    static WwiseCurvePoint[] Points(ref Reader r, uint count)
    {
        var points = new WwiseCurvePoint[r.Count(count, 12)];
        for (int i = 0; i < points.Length; i++) points[i] = new WwiseCurvePoint(r.F32(), r.F32(), r.U32());
        return points;
    }

    /// <summary>
    /// A random or sequence container: node base, loop and transition settings, modes, children and the weighted playlist.
    /// </summary>
    static WwiseRandomSequence RandomSequence(ref Reader r, uint id)
    {
        var node = NodeBase(ref r);
        ushort loop = r.U16(), loopMin = r.U16(), loopMax = r.U16();
        float transition = r.F32(), transitionMin = r.F32(), transitionMax = r.F32();
        ushort avoidRepeat = r.U16();
        byte transitionMode = r.U8(), randomMode = r.U8(), mode = r.U8(), flags = r.U8();
        var children = r.Ids(r.U32());
        var playlist = new (uint, int)[r.Count(r.U16(), 8)];
        for (int i = 0; i < playlist.Length; i++) playlist[i] = (r.U32(), r.I32());
        return new WwiseRandomSequence(id, node, loop, loopMin, loopMax, transition, transitionMin, transitionMax, avoidRepeat, transitionMode,
            randomMode, mode, flags, children, playlist);
    }

    /// <summary>
    /// A switch container: node base, the switch or state group, its default, the children, each switch's nodes and the per-node parameters.
    /// </summary>
    static WwiseSwitchContainer Switch(ref Reader r, uint id)
    {
        var node = NodeBase(ref r);
        byte groupType = r.U8();
        uint group = r.U32(), defaultSwitch = r.U32();
        bool continuous = r.U8() != 0;
        var children = r.Ids(r.U32());
        var switches = new (uint, uint[])[r.Count(r.U32(), 8)];
        for (int i = 0; i < switches.Length; i++) switches[i] = (r.U32(), r.Ids(r.U32()));
        var parameters = new (uint, byte, byte, int, int)[r.Count(r.U32(), 14)];
        for (int i = 0; i < parameters.Length; i++) parameters[i] = (r.U32(), r.U8(), r.U8(), r.I32(), r.I32());
        return new WwiseSwitchContainer(id, node, groupType, group, defaultSwitch, continuous, children, switches, parameters);
    }

    /// <summary>
    /// A bus (type 8) or auxiliary bus (type 20): u32 parent bus, the property list without ranges (bus volume is <c>0x05</c>), 13 bytes
    /// (positioning and recovery time, <b>Unknown</b>), f32 maximum duck volume, u32 duck count (always 0 in Kenshi; others are rejected), the
    /// effect slots (u8 count; if any u8 bypass bits, then per slot u8 index, u32 effect id, u8 share set, u8 rendered), 6 bytes
    /// (<b>Unknown</b>), the RTPC list and u32 state group count (0). docs/formats/wwise-bnk.md#buses.
    /// </summary>
    static WwiseObject Bus(uint id, ReadOnlySpan<byte> body, bool aux)
    {
        try
        {
            var r = new Reader(body);
            uint parent = r.U32();
            int n = r.U8();
            var ids = r.Bytes(n);
            var values = new Dictionary<byte, uint>(n);
            for (int i = 0; i < n; i++) values[ids[i]] = r.U32();
            r.Bytes(13);
            r.F32();
            uint ducks = r.U32();
            if (ducks != 0) throw new InvalidDataException($"{ducks} ducking entries, not decoded");
            int slotCount = r.U8();
            byte bypass = slotCount > 0 ? r.U8() : (byte)0;
            var slots = new WwiseEffectSlot[slotCount];
            for (int i = 0; i < slots.Length; i++) slots[i] = new WwiseEffectSlot(r.U8(), r.U32(), r.U8() != 0, r.U8() != 0);
            r.Bytes(6);
            var rtpcs = Rtpcs(ref r);
            uint states = r.U32();
            if (states != 0) throw new InvalidDataException($"{states} state groups on a bus, not decoded");
            if (r.Remaining != 0) throw new InvalidDataException($"{r.Remaining} bytes left over");
            return new WwiseBus(id, parent, new WwiseProperties(values, new Dictionary<byte, (float, float)>()), rtpcs, body.ToArray(), aux, slots,
                    bypass);
        }
        catch (InvalidDataException e)
        {
            return new WwiseRawObject(aux ? WwiseObjectType.AuxBus : WwiseObjectType.Bus, id, body.ToArray(), e.Message);
        }
    }

    /// <summary>
    /// An attenuation: u8 cone flag (then f32 inside and outside degrees, outside volume dB, low-pass, high-pass), 7 u8 curve indices (per usage:
    /// dry volume, game-defined aux send, user-defined aux send, low-pass, three unused; 0xFF none), u8 curve count, per curve u8 scaling and
    /// u16 point count and the points, then the RTPC list.
    /// </summary>
    static WwiseAttenuation Attenuation(ref Reader r, uint id)
    {
        WwiseCone? cone = null;
        if (r.U8() != 0) cone = new WwiseCone(r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        var use = new sbyte[7];
        for (int i = 0; i < use.Length; i++) use[i] = (sbyte)r.U8();
        var curves = new WwiseAttenuationCurve[r.U8()];
        for (int i = 0; i < curves.Length; i++)
        {
            byte scaling = r.U8();
            curves[i] = new WwiseAttenuationCurve(scaling, Points(ref r, r.U16()));
        }
        return new WwiseAttenuation(id, cone, use, curves, Rtpcs(ref r));
    }

    /// <summary>
    /// An effect or share set: u32 plug-in id, u32 parameter size and the parameter block, u8 media count (0 in Kenshi), the RTPC list on the
    /// effect's parameters (as node RTPCs; the u8 is the plug-in's parameter id) and u16 0 (<b>Unknown</b>).
    /// </summary>
    static WwiseEffect Effect(ref Reader r, WwiseObjectType type, uint id)
    {
        uint plugin = r.U32();
        var parameters = r.Bytes(r.Count(r.U32(), 1));
        byte media = r.U8();
        if (media != 0) throw new InvalidDataException($"{media} media on an effect, not decoded");
        var rtpcs = Rtpcs(ref r);
        if (r.U16() != 0) throw new InvalidDataException("Effect tail not zero");
        return new WwiseEffect(type, id, plugin, parameters, rtpcs);
    }

    /// <summary>
    /// A layer (blend) container: node base, children and the layers with their crossfade curves.
    /// </summary>
    static WwiseLayerContainer Layer(ref Reader r, uint id)
    {
        var node = NodeBase(ref r);
        var children = r.Ids(r.U32());
        var layers = new WwiseLayer[r.Count(r.U32(), 15)];
        for (int i = 0; i < layers.Length; i++)
        {
            uint layerId = r.U32();
            var rtpcs = Rtpcs(ref r);
            uint crossfade = r.U32();
            byte crossfadeType = r.U8();
            var assoc = new (uint, WwiseCurvePoint[])[r.Count(r.U32(), 8)];
            for (int j = 0; j < assoc.Length; j++) assoc[j] = (r.U32(), Points(ref r, r.U32()));
            layers[i] = new WwiseLayer(layerId, rtpcs, crossfade, crossfadeType, assoc);
        }
        return new WwiseLayerContainer(id, node, children, layers);
    }

    /// <summary>
    /// What all four music types share after their own leading fields: u8 flags, node base, child ids, grid (f64 period, f64 offset,
    /// f32 tempo, u8 beats, u8 beat unit, u8 flags) and stingers (none in Kenshi; not decoded).
    /// </summary>
    static WwiseMusicNode MusicNode(ref Reader r)
    {
        byte flags = r.U8();
        var node = NodeBase(ref r);
        var children = r.Ids(r.U32());
        var meter = new WwiseMeter(r.F64(), r.F64(), r.F32(), r.U8(), r.U8(), r.U8());
        uint stingers = r.U32();
        if (stingers != 0) throw new InvalidDataException($"{stingers} stingers, not decoded");
        return new WwiseMusicNode(flags, node, children, meter);
    }

    /// <summary>
    /// A music fade: s32 time (ms), u32 curve, s32 offset (ms).
    /// </summary>
    static WwiseFade Fade(ref Reader r) => new(r.I32(), r.U32(), r.I32());

    /// <summary>
    /// Transition rules of a music switch or playlist. A rule with a transition segment is not decoded (none in Kenshi).
    /// </summary>
    static WwiseMusicTransition[] Transitions(ref Reader r)
    {
        var rules = new WwiseMusicTransition[r.Count(r.U32(), 64)];
        for (int i = 0; i < rules.Length; i++)
        {
            var sources = r.Ids(r.U32());
            var destinations = r.Ids(r.U32());
            var source = new WwiseTransitionSource(Fade(ref r), r.U32(), r.U32(), r.U8() != 0);
            var destination = new WwiseTransitionDestination(Fade(ref r), r.U32(), r.U32(), r.U16(), r.U8() != 0, r.U8() != 0);
            if (r.U8() != 0) throw new InvalidDataException("Transition segment not decoded");
            rules[i] = new WwiseMusicTransition(sources, destinations, source, destination);
        }
        return rules;
    }

    /// <summary>
    /// A music segment: music node, length (ms) and its markers (id, position, ASCII name).
    /// </summary>
    static WwiseMusicSegment MusicSegment(ref Reader r, uint id)
    {
        var music = MusicNode(ref r);
        double duration = r.F64();
        var markers = new WwiseMarker[r.Count(r.U32(), 16)];
        for (int i = 0; i < markers.Length; i++)
        {
            uint markerId = r.U32();
            double position = r.F64();
            markers[i] = new WwiseMarker(markerId, position, System.Text.Encoding.ASCII.GetString(r.Bytes(r.Count(r.U32(), 1))));
        }
        return new WwiseMusicSegment(id, music, duration, markers);
    }

    /// <summary>
    /// A music track. Track types other than 0 (random, sequence, switch tracks) are not decoded (none in Kenshi).
    /// </summary>
    static WwiseMusicTrack MusicTrack(ref Reader r, uint id)
    {
        byte flags = r.U8();
        var sources = new WwiseSource[r.Count(r.U32(), 15)];
        for (int i = 0; i < sources.Length; i++) sources[i] = Source(ref r);
        var clips = new WwiseMusicClip[r.Count(r.U32(), 40)];
        for (int i = 0; i < clips.Length; i++) clips[i] = new WwiseMusicClip(r.U32(), r.U32(), r.F64(), r.F64(), r.F64(), r.F64());
        uint subTracks = r.U32();
        var automation = new WwiseClipAutomation[r.Count(r.U32(), 12)];
        for (int i = 0; i < automation.Length; i++) automation[i] = new WwiseClipAutomation(r.U32(), r.U32(), Points(ref r, r.U32()));
        var node = NodeBase(ref r);
        byte trackType = r.U8();
        if (trackType != 0) throw new InvalidDataException($"Track type {trackType} not decoded");
        return new WwiseMusicTrack(id, flags, sources, clips, subTracks, automation, node, trackType, r.I32());
    }

    /// <summary>
    /// A music switch: music node, transition rules, continue-playback flag, the decision tree's switch or state groups and the tree.
    /// </summary>
    static WwiseMusicSwitch MusicSwitch(ref Reader r, uint id)
    {
        var music = MusicNode(ref r);
        var transitions = Transitions(ref r);
        bool continuePlayback = r.U8() != 0;
        int depth = r.Count(r.U32(), 5);
        var groups = r.Ids((uint)depth);
        var arguments = new (uint, byte)[depth];
        for (int i = 0; i < depth; i++) arguments[i] = (groups[i], r.U8());
        int treeSize = r.Count(r.U32(), 1);
        byte mode = r.U8();
        if (treeSize % 12 != 0 || treeSize == 0) throw new InvalidDataException($"Decision tree of {treeSize} bytes");
        var flat = new (uint Key, uint Value, ushort Weight, ushort Probability)[treeSize / 12];
        for (int i = 0; i < flat.Length; i++) flat[i] = (r.U32(), r.U32(), r.U16(), r.U16());
        return new WwiseMusicSwitch(id, music, transitions, continuePlayback, arguments, mode, DecisionNode(flat, 0, 0, depth));
    }

    /// <summary>
    /// The decision tree is stored flat, 12 bytes a node: u32 key, then at the last depth u32 audio node id, above it u16 first child index and
    /// u16 child count; then u16 weight, u16 probability. The root (index 0) is above the first depth.
    /// </summary>
    static WwiseDecisionNode DecisionNode((uint Key, uint Value, ushort Weight, ushort Probability)[] flat, int index, int level, int depth)
    {
        var (key, value, weight, probability) = flat[index];
        if (level == depth) return new WwiseDecisionNode(key, value, weight, probability, []);
        int first = (int)(value & 0xFFFF), count = (int)(value >> 16);
        if (first + count > flat.Length || first <= index)
            throw new InvalidDataException($"Decision node {index} children {first}+{count} out of range");
        var children = new WwiseDecisionNode[count];
        for (int i = 0; i < count; i++) children[i] = DecisionNode(flat, first + i, level + 1, depth);
        return new WwiseDecisionNode(key, 0, weight, probability, children);
    }

    /// <summary>
    /// A music playlist: music node, transition rules and the playlist tree, stored depth-first.
    /// </summary>
    static WwiseMusicPlaylist MusicPlaylist(ref Reader r, uint id)
    {
        var music = MusicNode(ref r);
        var transitions = Transitions(ref r);
        int count = r.Count(r.U32(), 30);
        int read = 0;
        var root = PlaylistItem(ref r, ref read, count);
        if (read != count) throw new InvalidDataException($"Playlist tree holds {read} of {count} items");
        return new WwiseMusicPlaylist(id, music, transitions, root);
    }

    /// <summary>
    /// The playlist is stored depth-first: per item u32 segment id (0 for a group), u32 item id, u32 child count, u32 playlist type,
    /// s16 loop count, u16, u16, u32 weight, u16 avoid-repeat count, u8 using weight, u8 shuffle; then its children.
    /// </summary>
    static WwisePlaylistItem PlaylistItem(ref Reader r, ref int read, int count)
    {
        if (++read > count) throw new InvalidDataException("Playlist tree holds more items than its count");
        uint segment = r.U32(), itemId = r.U32();
        uint childCount = r.U32(), type = r.U32();
        short loop = (short)r.U16();
        ushort loopMin = r.U16(), loopMax = r.U16();
        uint weight = r.U32();
        ushort avoid = r.U16();
        bool usingWeight = r.U8() != 0, shuffle = r.U8() != 0;
        if (childCount > count - read)
            throw new InvalidDataException($"Playlist item {itemId:X8} has {childCount} children, more than the count");
        var children = new WwisePlaylistItem[childCount];
        for (int i = 0; i < children.Length; i++) children[i] = PlaylistItem(ref r, ref read, count);
        return new WwisePlaylistItem(segment, itemId, type, loop, loopMin, loopMax, weight, avoid, usingWeight, shuffle, children);
    }

    /// <summary>
    /// A bounds-checked little-endian cursor over one object's body; reading past it throws <see cref="InvalidDataException"/>.
    /// </summary>
    ref struct Reader(ReadOnlySpan<byte> span)
    {
        /// <summary>
        /// The object's body.
        /// </summary>
        readonly ReadOnlySpan<byte> _span = span;
        /// <summary>
        /// The read position in <see cref="_span"/>.
        /// </summary>
        int _p;

        /// <summary>
        /// Bytes not read yet; a decoded object must leave none.
        /// </summary>
        public readonly int Remaining => _span.Length - _p;

        /// <summary>
        /// The next <paramref name="n"/> bytes; <see cref="InvalidDataException"/> when fewer are left.
        /// </summary>
        ReadOnlySpan<byte> Take(int n)
        {
            if (n > _span.Length - _p)
                throw new InvalidDataException($"Read of {n} bytes at {_p} runs past the object ({_span.Length} bytes)");
            var s = _span.Slice(_p, n);
            _p += n;
            return s;
        }

        /// <summary>
        /// Reads a byte.
        /// </summary>
        public byte U8() => Take(1)[0];
        /// <summary>
        /// Reads a little-endian uint16.
        /// </summary>
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        /// <summary>
        /// Reads a little-endian uint32.
        /// </summary>
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        /// <summary>
        /// Reads a little-endian int32.
        /// </summary>
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        /// <summary>
        /// Reads a little-endian float.
        /// </summary>
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        /// <summary>
        /// Reads a little-endian double.
        /// </summary>
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        /// <summary>
        /// Reads <paramref name="n"/> bytes into a new array.
        /// </summary>
        public byte[] Bytes(int n) => Take(n).ToArray();

        /// <summary>
        /// A count, checked against what is left (each element at least <paramref name="minSize"/> bytes).
        /// </summary>
        public readonly int Count(uint n, int minSize)
        {
            if (n > (uint)(Remaining / minSize))
                throw new InvalidDataException($"Count {n} at {_p} is more than the object holds");
            return (int)n;
        }

        /// <summary>
        /// Reads <paramref name="n"/> uint32 ids.
        /// </summary>
        public uint[] Ids(uint n)
        {
            var ids = new uint[Count(n, 4)];
            for (int i = 0; i < ids.Length; i++) ids[i] = U32();
            return ids;
        }
    }
}