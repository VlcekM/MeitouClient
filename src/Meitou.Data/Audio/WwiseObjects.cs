namespace Meitou.Data.Audio;

/// <summary>
/// HIRC object types (docs/formats/wwise-bnk.md#hirc). The numbers are the format's; the names describe what each holds.
/// </summary>
public enum WwiseObjectType : byte
{
    /// <summary>
    /// HIRC type 1: a state's property changes; kept raw.
    /// </summary>
    State = 1,
    /// <summary>
    /// HIRC type 2: a sound (<see cref="WwiseSound"/>).
    /// </summary>
    Sound = 2,
    /// <summary>
    /// HIRC type 3: an action of an event (<see cref="WwiseAction"/>).
    /// </summary>
    Action = 3,
    /// <summary>
    /// HIRC type 4: an event, a list of actions (<see cref="WwiseEvent"/>).
    /// </summary>
    Event = 4,
    /// <summary>
    /// HIRC type 5: a random or sequence container (<see cref="WwiseRandomSequence"/>).
    /// </summary>
    RandomSequenceContainer = 5,
    /// <summary>
    /// HIRC type 6: a switch container (<see cref="WwiseSwitchContainer"/>).
    /// </summary>
    SwitchContainer = 6,
    /// <summary>
    /// HIRC type 7: an actor-mixer (<see cref="WwiseActorMixer"/>).
    /// </summary>
    ActorMixer = 7,
    /// <summary>
    /// HIRC type 8: a bus (<see cref="WwiseBus"/>).
    /// </summary>
    Bus = 8,
    /// <summary>
    /// HIRC type 9: a layer (blend) container (<see cref="WwiseLayerContainer"/>).
    /// </summary>
    LayerContainer = 9,
    /// <summary>
    /// HIRC type 10: a music segment (<see cref="WwiseMusicSegment"/>).
    /// </summary>
    MusicSegment = 10,
    /// <summary>
    /// HIRC type 11: a music track (<see cref="WwiseMusicTrack"/>).
    /// </summary>
    MusicTrack = 11,
    /// <summary>
    /// HIRC type 12: a music switch container (<see cref="WwiseMusicSwitch"/>).
    /// </summary>
    MusicSwitch = 12,
    /// <summary>
    /// HIRC type 13: a music random/sequence container, a playlist (<see cref="WwiseMusicPlaylist"/>).
    /// </summary>
    MusicRandomSequence = 13,
    /// <summary>
    /// HIRC type 14: a distance attenuation (<see cref="WwiseAttenuation"/>).
    /// </summary>
    Attenuation = 14,
    /// <summary>
    /// HIRC type 15: a dialogue event; kept raw.
    /// </summary>
    DialogueEvent = 15,
    /// <summary>
    /// HIRC type 16: a feedback bus; kept raw.
    /// </summary>
    FeedbackBus = 16,
    /// <summary>
    /// HIRC type 17: a feedback node; kept raw.
    /// </summary>
    FeedbackNode = 17,
    /// <summary>
    /// HIRC type 18: an effect share set (<see cref="WwiseEffect"/>).
    /// </summary>
    FxShareSet = 18,
    /// <summary>
    /// HIRC type 19: an effect (<see cref="WwiseEffect"/>).
    /// </summary>
    FxCustom = 19,
    /// <summary>
    /// HIRC type 20: an auxiliary bus (<see cref="WwiseBus"/> with <see cref="WwiseBus.IsAux"/>).
    /// </summary>
    AuxBus = 20,
}

/// <summary>
/// One HIRC object. Types the reader does not decode are kept as <see cref="WwiseRawObject"/>.
/// </summary>
public abstract record WwiseObject(WwiseObjectType Type, uint Id);

/// <summary>
/// An object kept undecoded: its type is not parsed (buses, effects, attenuations), or its body did not match the documented layout.
/// </summary>
public sealed record WwiseRawObject(WwiseObjectType Type, uint Id, byte[] Body, string? Error) : WwiseObject(Type, Id);

/// <summary>
/// An event: the actions it runs, in order. Its id is the hash of its name (<see cref="WwiseHash"/>).
/// </summary>
public sealed record WwiseEvent(uint Id, uint[] Actions) : WwiseObject(WwiseObjectType.Event, Id);

/// <summary>
/// An action. <see cref="Kind"/> is the high byte of the action type (what it does), <see cref="Scope"/> the low byte (which objects:
/// 2 the target on every game object, 3 the target on the posting game object, 4 all, 5 all but the exceptions on the posting object, 6 and 7
/// all but the exceptions; docs/formats/wwise-bnk.md "Action"). Kind-specific fields are set where they apply.
/// </summary>
public sealed record WwiseAction(uint Id, ushort ActionType, uint Target, bool TargetIsBus,
        WwiseProperties Properties) : WwiseObject(WwiseObjectType.Action, Id)
{
    /// <summary>
    /// What the action does: the high byte of <see cref="ActionType"/>.
    /// </summary>
    public WwiseActionKind Kind => (WwiseActionKind)(ActionType >> 8);
    /// <summary>
    /// Which objects the action applies to: the low byte of <see cref="ActionType"/>, numbered as in the Wwise SDK.
    /// </summary>
    public int Scope => ActionType & 0xFF;
    /// <summary>
    /// Stop, play and set-value actions: the shape of the fade curve.
    /// </summary>
    public byte FadeCurve { get; init; }
    /// <summary>
    /// Play: the bank holding the target.
    /// </summary>
    public uint BankId { get; init; }
    /// <summary>
    /// Set/reset pitch, volume, bus volume, low-pass: 1 = an offset, 2 = an absolute value.
    /// </summary>
    public byte ValueMeaning { get; init; }
    /// <summary>
    /// Set-value actions: the value, an offset or absolute as <see cref="ValueMeaning"/> says.
    /// </summary>
    public float Value { get; init; }
    /// <summary>
    /// Set-value actions: the lower end of the random range added to <see cref="Value"/>.
    /// </summary>
    public float ValueMin { get; init; }
    /// <summary>
    /// Set-value actions: the upper end of the random range added to <see cref="Value"/>.
    /// </summary>
    public float ValueMax { get; init; }
    /// <summary>
    /// Set state: the state group and the state.
    /// </summary>
    public uint GroupId { get; init; }
    /// <summary>
    /// Set state: the state set in <see cref="GroupId"/>.
    /// </summary>
    public uint StateId { get; init; }
    /// <summary>
    /// Bypass effects: whether to bypass, and the effect slot mask.
    /// </summary>
    public bool Bypass { get; init; }
    /// <summary>
    /// Bypass effects: the slots affected, bit n = slot n, 0x10 = all slots.
    /// </summary>
    public byte BypassMask { get; init; }
    /// <summary>
    /// Objects excluded from a stop or set-value action over all objects: (id, is a bus).
    /// </summary>
    public (uint Id, bool IsBus)[] Exceptions { get; init; } = [];
}

/// <summary>
/// The action kinds seen in Kenshi's banks (high byte of the action type).
/// </summary>
public enum WwiseActionKind : byte
{
    /// <summary>
    /// Kind 0x01: stops the target, fading out.
    /// </summary>
    Stop = 0x01,
    /// <summary>
    /// Kind 0x02: pauses the target.
    /// </summary>
    Pause = 0x02,
    /// <summary>
    /// Kind 0x03: resumes a paused target.
    /// </summary>
    Resume = 0x03,
    /// <summary>
    /// Kind 0x04: plays the target, loading it from <see cref="WwiseAction.BankId"/>.
    /// </summary>
    Play = 0x04,
    /// <summary>
    /// Kind 0x08: sets the target's pitch.
    /// </summary>
    SetPitch = 0x08,
    /// <summary>
    /// Kind 0x09: resets the target's pitch.
    /// </summary>
    ResetPitch = 0x09,
    /// <summary>
    /// Kind 0x0A: sets the target's volume.
    /// </summary>
    SetVolume = 0x0A,
    /// <summary>
    /// Kind 0x0B: resets the target's volume.
    /// </summary>
    ResetVolume = 0x0B,
    /// <summary>
    /// Kind 0x0C: sets a bus's volume.
    /// </summary>
    SetBusVolume = 0x0C,
    /// <summary>
    /// Kind 0x0D: resets a bus's volume.
    /// </summary>
    ResetBusVolume = 0x0D,
    /// <summary>
    /// Kind 0x0E: sets the target's low-pass filter.
    /// </summary>
    SetLowPass = 0x0E,
    /// <summary>
    /// Kind 0x0F: resets the target's low-pass filter.
    /// </summary>
    ResetLowPass = 0x0F,
    /// <summary>
    /// Kind 0x12: sets a state of a state group.
    /// </summary>
    SetState = 0x12,
    /// <summary>
    /// Kind 0x1A: bypasses or unbypasses effect slots of the target.
    /// </summary>
    BypassFx = 0x1A,
}

/// <summary>
/// A node's property list: values by property id (raw 32 bits; most are floats, see <see cref="WwisePropertyId"/>), and the random ranges
/// added to some of them each time the node plays.
/// </summary>
public sealed record WwiseProperties(IReadOnlyDictionary<byte, uint> Values, IReadOnlyDictionary<byte, (float Min, float Max)> Ranges)
{
    /// <summary>
    /// A property list with no values and no ranges.
    /// </summary>
    public static readonly WwiseProperties Empty = new(new Dictionary<byte, uint>(), new Dictionary<byte, (float, float)>());

    /// <summary>
    /// Property <paramref name="id"/> as a float; <paramref name="fallback"/> when the list does not have it.
    /// </summary>
    public float Float(byte id, float fallback = 0) => Values.TryGetValue(id, out var v) ? BitConverter.UInt32BitsToSingle(v) : fallback;
    /// <summary>
    /// Property <paramref name="id"/> as an int; <paramref name="fallback"/> when the list does not have it.
    /// </summary>
    public int Int(byte id, int fallback = 0) => Values.TryGetValue(id, out var v) ? (int)v : fallback;
}

/// <summary>
/// Property ids whose meaning is established (docs/formats/wwise-bnk.md#properties).
/// </summary>
public static class WwisePropertyId
{
    /// <summary>
    /// Volume offset in dB (float).
    /// </summary>
    public const byte Volume = 0x00;
    /// <summary>
    /// Pitch offset in cents (float).
    /// </summary>
    public const byte Pitch = 0x02;
    /// <summary>
    /// Low-pass filter amount 0..100 (float).
    /// </summary>
    public const byte LowPass = 0x03;
    /// <summary>
    /// On actions: the delay before the action runs, in ms (int; 6000 on some actions, Observed).
    /// </summary>
    public const byte Delay = 0x0E;
    /// <summary>
    /// On actions: the fade time, in ms (int).
    /// </summary>
    public const byte FadeTime = 0x0F;
    /// <summary>
    /// On buses: the bus volume in dB (float).
    /// </summary>
    public const byte BusVolume = 0x05;
    /// <summary>
    /// Loop count: 0 = forever, 1 = once (int).
    /// </summary>
    public const byte Loop = 0x3A;
}

/// <summary>
/// Where a sound's audio is: an embedded WEM (<see cref="StreamType"/> 0), <see cref="FileId"/> the bank holding it, <see cref="Offset"/> its offset
/// in the bank file (not in DATA) and <see cref="Size"/> its length, both equal to the bank's DIDX entry for <see cref="SourceId"/>.
/// </summary>
public sealed record WwiseSource(uint PluginId, byte StreamType, uint SourceId, uint FileId, uint Offset, uint Size, byte SourceBits,
        byte[] PluginParameters)
{
    /// <summary>
    /// The codec or source plugin: 0x00040001 Wwise Vorbis, 0x00650002 the silence source.
    /// </summary>
    public bool IsVorbis => PluginId == 0x00040001;
}

/// <summary>
/// A point of an RTPC curve or a layer's crossfade curve: x, y and the interpolation to the next point.
/// </summary>
public readonly record struct WwiseCurvePoint(float X, float Y, uint Interpolation);

/// <summary>
/// A game parameter driving one of a node's properties through a curve.
/// </summary>
public sealed record WwiseRtpc(uint GameParameterId, byte RtpcType, byte Accumulation, byte ParameterId, uint CurveId, byte Scaling,
        WwiseCurvePoint[] Points);

/// <summary>
/// A state group whose states change some of the node's properties.
/// </summary>
public sealed record WwiseStateGroup(uint GroupId, byte SyncType, (uint StateId, uint StateObjectId)[] States);

/// <summary>
/// A point on a 3D path, relative to the listener, with the time to reach it (ms).
/// </summary>
public readonly record struct WwisePathVertex(float X, float Y, float Z, int Duration);

/// <summary>
/// A node's positioning when it overrides its parent's: <see cref="Bits"/> as stored; bit 3 set means 3D with an attenuation, and a 3D type of 3
/// (bits 5-6) adds a set of paths the sound moves along around the listener.
/// </summary>
public sealed record WwisePositioning(byte Bits, byte Bits3D, uint AttenuationId, byte PathMode, int TransitionTime, WwisePathVertex[] Vertices,
    (uint FirstVertex, uint VertexCount)[] Paths, (float X, float Y, float Z)[] PathRanges)
{
    /// <summary>
    /// Whether the node sets its own positioning instead of its parent's (bit 0).
    /// </summary>
    public bool OverridesParent => (Bits & 1) != 0;
    /// <summary>
    /// Whether the node is positioned in 3D with an attenuation (bit 3).
    /// </summary>
    public bool Is3D => (Bits & 8) != 0;
    /// <summary>
    /// The 3D position type, bits 5-6; 3 adds paths around the listener.
    /// </summary>
    public int PositionType3D => (Bits >> 5) & 3;
    /// <summary>
    /// The sound moves along the stored paths around the listener (3D type 3 with vertices).
    /// </summary>
    public bool HasPaths => Is3D && PositionType3D == 3 && Vertices.Length > 0;
    /// <summary>
    /// The sound is placed by its game object and attenuated by distance (3D without paths).
    /// </summary>
    public bool IsGameDefined => Is3D && !HasPaths;
}

/// <summary>
/// The parameters every sound and container carries (docs/formats/wwise-bnk.md#node-base).
/// </summary>
public sealed record WwiseNodeBase(
    bool OverrideParentFx, (byte Index, uint FxId, bool IsShareSet, bool IsRendered)[] Effects, byte FxBypass, byte Unknown,
    uint OverrideBusId, uint ParentId, byte PriorityBits, WwiseProperties Properties, WwisePositioning Positioning,
    byte AuxBits, uint[] AuxBuses, byte AdvancedBits, byte VirtualQueueBehaviour, ushort MaxInstances,
        byte BelowThresholdBehaviour, byte AdvancedBits2,
    WwiseStateGroup[] StateGroups, WwiseRtpc[] Rtpcs);

/// <summary>
/// A sound: one source (a WEM) and its node parameters.
/// </summary>
public sealed record WwiseSound(uint Id, WwiseSource Source, WwiseNodeBase Node) : WwiseObject(WwiseObjectType.Sound, Id);

/// <summary>
/// An actor-mixer: a grouping node whose parameters its children inherit.
/// </summary>
public sealed record WwiseActorMixer(uint Id, WwiseNodeBase Node, uint[] Children) : WwiseObject(WwiseObjectType.ActorMixer, Id);

/// <summary>
/// A random or sequence container: plays one child at a time from <see cref="Playlist"/> (weights out of 50000 = 1.0), with a transition
/// between them. <see cref="Mode"/> 0 = random, 1 = sequence; <see cref="LoopCount"/> 0 = forever.
/// </summary>
public sealed record WwiseRandomSequence(uint Id, WwiseNodeBase Node, ushort LoopCount, ushort LoopModMin, ushort LoopModMax,
    float TransitionTime, float TransitionTimeModMin, float TransitionTimeModMax, ushort AvoidRepeatCount, byte TransitionMode, byte RandomMode,
    byte Mode, byte Flags, uint[] Children, (uint Id, int Weight)[] Playlist) : WwiseObject(WwiseObjectType.RandomSequenceContainer, Id);

/// <summary>
/// A switch container: plays the children assigned to the current state or switch of <see cref="GroupId"/>.
/// </summary>
public sealed record WwiseSwitchContainer(uint Id, WwiseNodeBase Node, byte GroupType, uint GroupId, uint DefaultSwitch, bool ContinuousValidation,
    uint[] Children, (uint SwitchId, uint[] Nodes)[] Switches, (uint NodeId, byte Bits, byte Bits2, int FadeOut, int FadeIn)[] NodeParameters)
    : WwiseObject(WwiseObjectType.SwitchContainer, Id);

/// <summary>
/// A layer of a layer container: its children's volume follows <see cref="CrossfadeParameterId"/> through each child's curve.
/// </summary>
public sealed record WwiseLayer(uint Id, WwiseRtpc[] Rtpcs, uint CrossfadeParameterId, byte CrossfadeParameterType, (uint ChildId,
        WwiseCurvePoint[] Curve)[] Associations);

/// <summary>
/// A layer container (blend container): plays all its children at once.
/// </summary>
public sealed record WwiseLayerContainer(uint Id, WwiseNodeBase Node, uint[] Children,
        WwiseLayer[] Layers) : WwiseObject(WwiseObjectType.LayerContainer, Id);

/// <summary>
/// An effect slot of a bus (or node): the slot index, the effect (<see cref="WwiseEffect"/>, or a share set) and whether it is a share set.
/// </summary>
public readonly record struct WwiseEffectSlot(byte Index, uint EffectId, bool IsShareSet, bool IsRendered);

/// <summary>
/// A bus (type 8) or an auxiliary bus (type 20, <see cref="IsAux"/>), partly decoded: its parent bus, its properties (bus volume <c>0x05</c>
/// in dB), its effect slots with their initial bypass bits (<see cref="FxBypass"/>, bit n = slot n bypassed) and its RTPCs (the user volume
/// sliders), found by shape in the undecoded rest (<see cref="Body"/>). See docs/formats/wwise-bnk.md#buses.
/// </summary>
public sealed record WwiseBus(uint Id, uint ParentBusId, WwiseProperties Properties, WwiseRtpc[] Rtpcs, byte[] Body,
    bool IsAux = false, WwiseEffectSlot[]? Slots = null, byte FxBypass = 0)
    : WwiseObject(IsAux ? WwiseObjectType.AuxBus : WwiseObjectType.Bus, Id)
{
    /// <summary>
    /// The bus's effect slots; empty when it has none.
    /// </summary>
    public WwiseEffectSlot[] Effects { get; init; } = Slots ?? [];
}

/// <summary>
/// An attenuation (type 14): how a 3D sound changes with its distance to the listener. <see cref="Curves"/> are shared by the usages:
/// <see cref="CurveToUse"/> gives, per usage (<see cref="VolumeDry"/> ...), the index of its curve (-1: none, the usage is not attenuated).
/// The cone, when enabled, attenuates by the angle off the emitter's front. Curve x is the distance, volume y is linear gain - 1
/// (Observed, as the RTPC volume curves), low-pass y is the low-pass amount 0..100.
/// </summary>
public sealed record WwiseAttenuation(uint Id, WwiseCone? Cone, sbyte[] CurveToUse, WwiseAttenuationCurve[] Curves, WwiseRtpc[] Rtpcs)
    : WwiseObject(WwiseObjectType.Attenuation, Id)
{
    /// <summary>
    /// The usages, as indices into <see cref="CurveToUse"/>: dry volume, game-defined and user-defined auxiliary sends, low-pass.
    /// </summary>
    public const int VolumeDry = 0, AuxGameDefined = 1, AuxUserDefined = 2, LowPass = 3;

    /// <summary>
    /// The curve of a usage, or null when it has none.
    /// </summary>
    public WwiseAttenuationCurve? Curve(int usage) =>
        usage < CurveToUse.Length && CurveToUse[usage] is >= 0 and var i && i < Curves.Length ? Curves[i] : null;
}

/// <summary>
/// An attenuation curve: <see cref="Scaling"/> as the RTPC curves (2 = dB for volumes, 0 none) and its points (x = distance).
/// </summary>
public sealed record WwiseAttenuationCurve(byte Scaling, WwiseCurvePoint[] Points);

/// <summary>
/// The cone of an attenuation: full inside <see cref="InsideDegrees"/>, <see cref="OutsideVolume"/> (dB) and <see cref="LowPass"/> beyond
/// <see cref="OutsideDegrees"/>.
/// </summary>
public sealed record WwiseCone(float InsideDegrees, float OutsideDegrees, float OutsideVolume, float LowPass, float HighPass);

/// <summary>
/// An effect (type 19) or an effect share set (type 18): the plug-in (<see cref="PluginId"/>: company in the high word, 0x0076 is Wwise RoomVerb,
/// 0x0069 parametric EQ, 0x006C compressor; type nibble 3), its parameter block (plug-in specific) and the RTPCs on its parameters
/// (<see cref="WwiseRtpc.ParameterId"/> is the plug-in's parameter id).
/// </summary>
public sealed record WwiseEffect(WwiseObjectType Type, uint Id, uint PluginId, byte[] Parameters, WwiseRtpc[] Rtpcs) : WwiseObject(Type, Id)
{
    /// <summary>
    /// The plug-in id of Wwise RoomVerb.
    /// </summary>
    public const uint RoomVerbPlugin = 0x00760003;
    /// <summary>
    /// Whether the effect is a RoomVerb with its full parameter block (at least 20 floats).
    /// </summary>
    public bool IsRoomVerb => PluginId == RoomVerbPlugin && Parameters.Length >= 80;

    /// <summary>
    /// RoomVerb's float parameters in the block's order (Observed from the six Kenshi effects; names are the plug-in's, guessed by value):
    /// 0 decay time (s), 1 HF damping (kHz), 2 diffusion (%), 3 stereo width (degrees), 4-12 three filter bands (gain dB, frequency Hz, Q),
    /// 13-15 front, rear, center level (dB), 16 dry level (dB), 17-19 levels (dB, see docs/audio.md#reverb).
    /// </summary>
    public float RoomVerbValue(int index) => BitConverter.ToSingle(Parameters, index * 4);
}

/// <summary>
/// A fade: its length (ms), the curve shape, and its offset (ms) from the point it is aligned to (negative = earlier).
/// </summary>
public readonly record struct WwiseFade(int Time, uint Curve, int Offset);

/// <summary>
/// The musical grid of a music node: grid period and offset (ms), tempo (beats per minute) and time signature. <see cref="Flags"/> nonzero =
/// the node sets its own grid instead of inheriting it (Observed).
/// </summary>
public readonly record struct WwiseMeter(double GridPeriod, double GridOffset, float Tempo, byte Beats, byte BeatUnit, byte Flags);

/// <summary>
/// What every music object (segment, track, switch, playlist) shares: node base, children and grid (docs/formats/wwise-bnk.md#music).
/// </summary>
public sealed record WwiseMusicNode(byte Flags, WwiseNodeBase Node, uint[] Children, WwiseMeter Meter);

/// <summary>
/// Where a music transition leaves the playing segment: fade-out, when (sync type), which cue, and whether its post-exit plays.
/// </summary>
public sealed record WwiseTransitionSource(WwiseFade FadeOut, uint SyncType, uint CueFilter, bool PlayPostExit);

/// <summary>
/// Where a music transition enters the next segment: fade-in, cue filter, the playlist item to jump to, entry type, pre-entry.
/// </summary>
public sealed record WwiseTransitionDestination(WwiseFade FadeIn, uint CueFilter, uint JumpToId, ushort EntryType, bool PlayPreEntry,
        bool MatchSourceCueName);

/// <summary>
/// A transition rule between music objects (<c>0xFFFFFFFF</c> = any) in a music switch or playlist.
/// </summary>
public sealed record WwiseMusicTransition(uint[] Sources, uint[] Destinations, WwiseTransitionSource Source, WwiseTransitionDestination Destination)
{
    /// <summary>
    /// The source or destination id that matches any music object.
    /// </summary>
    public const uint Any = 0xFFFFFFFF;
}

/// <summary>
/// A marker (cue) of a music segment: id, position (ms) and optional name.
/// </summary>
public sealed record WwiseMarker(uint Id, double Position, string Name)
{
    /// <summary>
    /// The marker id of a segment's entry cue (always first, Observed).
    /// </summary>
    public const uint Entry = 0x0298DF12;
    /// <summary>
    /// The marker id of a segment's exit cue (always last, Observed).
    /// </summary>
    public const uint Exit = 0x5BBBD648;
}

/// <summary>
/// A music segment: its tracks (<see cref="WwiseMusicNode.Children"/>), length (ms) and markers (entry cue, exit cue, custom cues).
/// </summary>
public sealed record WwiseMusicSegment(uint Id, WwiseMusicNode Music, double Duration,
        WwiseMarker[] Markers) : WwiseObject(WwiseObjectType.MusicSegment, Id)
{
    /// <summary>
    /// The position of the entry cue (ms); 0 without one.
    /// </summary>
    public double EntryCue => Markers.FirstOrDefault(m => m.Id == WwiseMarker.Entry)?.Position ?? 0;
    /// <summary>
    /// The position of the exit cue (ms); the segment's end without one.
    /// </summary>
    public double ExitCue => Markers.FirstOrDefault(m => m.Id == WwiseMarker.Exit)?.Position ?? Duration;
}

/// <summary>
/// A clip of a music track: <see cref="SourceId"/> placed at <see cref="PlayAt"/> (ms, segment time of the source's start), trimmed by
/// <see cref="BeginTrim"/> and <see cref="EndTrim"/> (ms; end trim ≤ 0), <see cref="SourceDuration"/> the source's full length (ms).
/// </summary>
public sealed record WwiseMusicClip(uint SubTrack, uint SourceId, double PlayAt, double BeginTrim, double EndTrim, double SourceDuration);

/// <summary>
/// An automation curve on a clip (volume or fades) by clip index.
/// </summary>
public sealed record WwiseClipAutomation(uint ClipIndex, uint AutomationType, WwiseCurvePoint[] Points);

/// <summary>
/// A music track: its sources (WEMs) and their clips in the segment. <see cref="TrackType"/> 0 = normal.
/// </summary>
public sealed record WwiseMusicTrack(uint Id, byte Flags, WwiseSource[] Sources, WwiseMusicClip[] Clips, uint SubTrackCount,
        WwiseClipAutomation[] Automation,
    WwiseNodeBase Node, byte TrackType, int LookAheadTime) : WwiseObject(WwiseObjectType.MusicTrack, Id);

/// <summary>
/// A node of a music switch's decision tree: the switch or state <see cref="Key"/> (0 = any, the fallback) at its depth; at the last depth
/// <see cref="AudioNodeId"/> is the music object it plays.
/// </summary>
public sealed record WwiseDecisionNode(uint Key, uint AudioNodeId, ushort Weight, ushort Probability, WwiseDecisionNode[] Children);

/// <summary>
/// A music switch: plays the child the decision tree picks for the current values of <see cref="Arguments"/>.
/// </summary>
public sealed record WwiseMusicSwitch(uint Id, WwiseMusicNode Music, WwiseMusicTransition[] Transitions, bool ContinuePlayback,
    (uint GroupId, byte GroupType)[] Arguments, byte Mode, WwiseDecisionNode Tree) : WwiseObject(WwiseObjectType.MusicSwitch, Id);

/// <summary>
/// An item of a music playlist: a group (<see cref="SegmentId"/> 0, <see cref="Children"/> played as <see cref="PlaylistType"/>: 0 sequence
/// continuous, 1 sequence step, 2 random continuous, 3 random step; Observed) or a segment (type <c>0xFFFFFFFF</c>).
/// <see cref="LoopCount"/> 0 = forever; <see cref="Weight"/> out of 50000 = 1.0.
/// </summary>
public sealed record WwisePlaylistItem(uint SegmentId, uint ItemId, uint PlaylistType, short LoopCount, ushort LoopModMin, ushort LoopModMax,
        uint Weight,
    ushort AvoidRepeatCount, bool UsingWeight, bool Shuffle, WwisePlaylistItem[] Children)
{
    /// <summary>
    /// Whether the item is a group of items rather than a segment.
    /// </summary>
    public bool IsGroup => Children.Length > 0 || SegmentId == 0;
}

/// <summary>
/// A music playlist (music random/sequence container): plays its segments in the order of the playlist tree.
/// </summary>
public sealed record WwiseMusicPlaylist(uint Id, WwiseMusicNode Music, WwiseMusicTransition[] Transitions, WwisePlaylistItem Playlist)
    : WwiseObject(WwiseObjectType.MusicRandomSequence, Id);