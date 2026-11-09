using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The body area an ANIMATION plays on (<c>layer</c>: all, lower, upper, overlay).
/// </summary>
public enum AnimationArea : byte
{
    /// <summary>
    /// <c>all</c>: the whole body.
    /// </summary>
    All,
    /// <summary>
    /// <c>lower</c>: the lower body.
    /// </summary>
    Lower,
    /// <summary>
    /// <c>upper</c>: the upper body (the default).
    /// </summary>
    Upper,
    /// <summary>
    /// <c>overlay</c>.
    /// </summary>
    Overlay,
    /// <summary>
    /// Any other <c>layer</c> text.
    /// </summary>
    Other
}

/// <summary>
/// The weapon kinds an ANIMATION's validity flags name (katanas, sabre, blunt, heavy weapons, hackers, polearm, unarmed, 1 handed).
/// </summary>
[Flags]
public enum WeaponKinds : ushort
{
    /// <summary>
    /// No kind.
    /// </summary>
    None = 0,
    /// <summary>
    /// <c>katanas</c>.
    /// </summary>
    Katana = 1,
    /// <summary>
    /// <c>sabre</c>.
    /// </summary>
    Sabre = 2,
    /// <summary>
    /// <c>blunt</c>.
    /// </summary>
    Blunt = 4,
    /// <summary>
    /// <c>heavy weapons</c>.
    /// </summary>
    Heavy = 8,
    /// <summary>
    /// <c>hackers</c>.
    /// </summary>
    Hacker = 16,
    /// <summary>
    /// <c>polearm</c>.
    /// </summary>
    Polearm = 32,
    /// <summary>
    /// <c>unarmed</c>: fists.
    /// </summary>
    Unarmed = 64,
    /// <summary>
    /// <c>1 handed</c>; also what a weapon of no skill class animates as (<see cref="Combat.WeaponCategories.AnimationKind"/>).
    /// </summary>
    OneHanded = 128,
    /// <summary>
    /// Every kind.
    /// </summary>
    All = 255,
}

/// <summary>
/// The usable ANIMATION records of a game database (normal category, not disabled, no weather overlays; docs/animation.md "Animation definitions"),
/// and the choices a character makes from them. <b>Observed</b>, not traced in the original (docs/simulation.md "Animation"): movement clips of a
/// layer form a chain by <c>move speed</c> and a character blends the two around its speed; a standing character plays one of the valid idles,
/// another after <c>idle time</c> seconds.
/// </summary>
public sealed class AnimationLibrary
{
    /// <summary>
    /// Every definition, in the library's index order.
    /// </summary>
    readonly List<AnimationDefinition> _all;

    /// <summary>
    /// An ANIMATION record's string id to its place in the original's list order.
    /// </summary>
    readonly Dictionary<string, int> _dataOrder = new(StringComparer.Ordinal);

    /// <summary>
    /// The category of the animal definitions (ANIMAL_ANIMATION records and creature techniques): no ANIMATION record has it, so no humanoid selector
    /// takes them.
    /// </summary>
    public const int AnimalCategory = -1;

    /// <summary>
    /// The category of weather-type records with stumbles: no selector takes them, only <see cref="Stumbles"/> by their
    /// <see cref="AnimationDefinition.StumbleCategory"/> (docs/animation.md).
    /// </summary>
    public const int WeatherCategory = -2;

    /// <summary>
    /// CharacterAnimCategory 8, RANGED.
    /// </summary>
    public const int RangedCategory = 8;

    /// <summary>
    /// The ANIMATION category of <c>carry me</c> (3 in the data; the enum name is not known here).
    /// </summary>
    public const int CarriedCategory = 3;

    /// <summary>
    /// CharacterAnimCategory 4, SWIMMING (<b>Verified</b>, docs/animation.md): what a character in deep water plays instead of the normal clips.
    /// </summary>
    public const int SwimmingCategory = 4;

    /// <summary>
    /// CharacterAnimCategory 5, GROUNDED: what a crawling character plays (<c>AnimHuman_LowerCombat</c> picks it for posture 1 and 2,
    /// <b>Observed</b>).
    /// </summary>
    public const int GroundedCategory = 5;

    /// <summary>
    /// The RANGED ANIMATION record of the crossbow's aim (<c>aimH</c>, clip <c>aimshoot</c>) and the default reload (<c>reload 1 phase</c>), by
    /// record name.
    /// </summary>
    public const string RangedAimRecord = "aimH", RangedReloadRecord = "reload 1 phase";

    /// <summary>
    /// A library of <paramref name="definitions"/>, in their order (their positions are the indices).
    /// </summary>
    public AnimationLibrary(IEnumerable<AnimationDefinition> definitions)
    {
        _all = [.. definitions];
        for (int i = 0; i < _all.Count; i++) if (_all[i].Technique) _techniques.TryAdd(_all[i].Id, i);
        _swimming = [.. Enumerable.Range(0, _all.Count).Where(i => _all[i].Category == SwimmingCategory && !_all[i].Technique)];
        _grounded = [.. Enumerable.Range(0, _all.Count).Where(i => _all[i].Category == GroundedCategory && !_all[i].Technique)];
        _leftBounds = Bounds(_all.SelectMany(d =>
            new[] { d.LeftLegMin, d.LeftLegMax, AnimationDefinition.LegFloor(d.LeftLegMin), AnimationDefinition.LegCeil(d.LeftLegMax) }));
        _rightBounds = Bounds(_all.SelectMany(d =>
            new[] { d.RightLegMin, d.RightLegMax, AnimationDefinition.LegFloor(d.RightLegMin), AnimationDefinition.LegCeil(d.RightLegMax) }));
    }

    /// <summary>
    /// Every definition, by index.
    /// </summary>
    public IReadOnlyList<AnimationDefinition> Definitions => _all;

    /// <summary>
    /// The usable ANIMATION records (normal category, not disabled, no weather overlay), then the COMBAT_TECHNIQUE records of humanoids
    /// (<c>animal</c> 8 or less, the set Kenshi's startup preprocessing gathers; docs/animation.md) as
    /// <see cref="AnimationDefinition.FromTechnique"/>, then the RANGED (category 8) records, which only the combat system plays (appended last so
    /// the indices of the others do not move).
    /// </summary>
    public static AnimationLibrary FromDatabase(GameDatabase db)
    {
        var library = new AnimationLibrary(Humanoid(db)
            // The animals' after everything (no earlier index moves): every ANIMAL_ANIMATION, then the creature techniques (animal 9 and up),
            // which only animals play.
            .Concat(db.OfType(FcsRecordType.AnimalAnimation)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.FromAnimal))
            .Concat(db.OfType(FcsRecordType.CombatTechnique)
                .Where(r => r.GetInt("animal") > 8)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(r => AnimationDefinition.FromTechnique(r) with { Category = AnimalCategory }))
            // The swimming records (category 4: swim, swim carry, swim idle) last of all, again so no earlier index moves; only
            // <see cref="SwimMovement"/> and <see cref="SwimIdle"/> choose them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("category") == SwimmingCategory && r.GetInt("weather type") == 0)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.From))
            // The grounded records (category 5: crawling move, crawl idle up / down, crawl use) after those; only the crawl selectors choose them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("category") == GroundedCategory && r.GetInt("weather type") == 0)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.From))
            // The weather-type records that list `stumbles` (FUN_1405bbd40 files them in the by-name and stumble maps like any record), last of all;
            // only the stumble pick takes them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("weather type") != 0 && r.GetReferences("stumbles").Any())
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(r => AnimationDefinition.From(r) with { StumbleCategory = r.GetInt("category"), Category = WeatherCategory })));
        library.BuildAnimals(db);
        // The order the original lists ANIMATION records in: its per-type map of records by string id (GameDataContainer +0x90), a boost hash map
        // filled in load order.
        int rank = 0;
        var animationIds = db.OfType(FcsRecordType.Animation).Select(r => r.StringId);
        foreach (var id in BoostHashOrder.Of(animationIds, BoostHashOrder.StringHash))
            library._dataOrder[id] = rank++;
        return library;
    }

    /// <summary>
    /// The humanoid part of <see cref="FromDatabase"/> (its order is what the hashed library indices of people are).
    /// </summary>
    static IEnumerable<AnimationDefinition> Humanoid(GameDatabase db) => db.OfType(FcsRecordType.Animation)
        .Where(r => !r.GetBool("disabled") && r.GetInt("category") == 0 && r.GetInt("weather type") == 0)
        .OrderBy(r => r.StringId, StringComparer.Ordinal)
        .Select(AnimationDefinition.From)
        .Concat(db.OfType(FcsRecordType.CombatTechnique)
            .Where(r => r.GetInt("animal") <= 8)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.FromTechnique))
        .Concat(db.OfType(FcsRecordType.Animation)
            .Where(r => !r.GetBool("disabled") && r.GetInt("category") == RangedCategory && r.GetInt("weather type") == 0)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.From))
        // Category 3 after those (so no earlier index moves): the carried body's held pose "carry me". Played by name only; every selector takes
        // category 0.
        .Concat(db.OfType(FcsRecordType.Animation)
            .Where(r => !r.GetBool("disabled") && r.GetInt("category") == CarriedCategory && r.GetInt("weather type") == 0)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.From));

    /// <summary>
    /// A definition's place in the original's list of ANIMATION records (<see cref="BoostHashOrder"/>), for its "first in list order" ties; its index
    /// without one.
    /// </summary>
    int DataOrder(int index) => _dataOrder.TryGetValue(_all[index].Id, out int rank) ? rank : int.MaxValue / 2 + index;

    #region Lookup

    /// <summary>
    /// A COMBAT_TECHNIQUE's string id to its definition's index.
    /// </summary>
    readonly Dictionary<string, int> _techniques = new(StringComparer.Ordinal);

    /// <summary>
    /// The index of the definition (ANIMATION or COMBAT_TECHNIQUE) whose record is called <paramref name="recordName"/>, or -1.
    /// </summary>
    public int IndexOfAny(string recordName) => _all.FindIndex(d => d.Name == recordName);

    /// <summary>
    /// The definition's index (an ANIMATION record by name), or -1.
    /// </summary>
    public int IndexOf(string recordName) => _all.FindIndex(d => d.Name == recordName && !d.Technique);

    /// <summary>
    /// The index of a COMBAT_TECHNIQUE's definition by its string id, or -1.
    /// </summary>
    public int IndexOfTechnique(string stringId) => _techniques.TryGetValue(stringId, out int i) ? i : -1;

    /// <summary>
    /// The index of an ANIMATION record's definition by its string id, or -1.
    /// </summary>
    public int IndexOfRecord(string stringId) => _all.FindIndex(d => d.Id == stringId && !d.Technique);

    #endregion

    #region Stance cache keys

    /// <summary>
    /// The leg health values the records' ranges are cut at (sorted, distinct), per leg. A stance is cached by the interval of these its leg values
    /// fall in, not by the values: they change every tick of an injury that heals or rots, and a key per value would grow the caches without bound.
    /// </summary>
    readonly float[] _leftBounds, _rightBounds;

    /// <summary>
    /// The values sorted and distinct, NaN left out.
    /// </summary>
    static float[] Bounds(IEnumerable<float> values) => [.. values.Where(v => !float.IsNaN(v)).Distinct().Order()];

    /// <summary>
    /// A leg health standing for every value that compares the same with each bound (below, equal to, above): the bound itself when it is one, else
    /// the nearest float inside the interval. <see cref="AnimationDefinition.Fits"/> only compares, so the clips chosen for the stand-in are the
    /// clips for the value.
    /// </summary>
    static float Representative(float health, float[] bounds)
    {
        if (bounds.Length == 0) return 100;
        int at = Array.BinarySearch(bounds, health);
        if (at >= 0) return bounds[at];
        int above = ~at;   // the first bound over the value (a NaN sorts first, and fits nothing, like a value under every bound)
        if (above == 0) return MathF.BitDecrement(bounds[0]);
        return MathF.BitIncrement(bounds[above - 1]);
    }

    /// <summary>
    /// The stance with each leg value replaced by its <see cref="Representative"/> (the caches' key).
    /// </summary>
    AnimationStance Canonical(in AnimationStance s) =>
        s with { LeftLeg = Representative(s.LeftLeg, _leftBounds), RightLeg = Representative(s.RightLeg, _rightBounds) };

    /// <summary>
    /// The stance with the legs neutral: the idle caches hold the clips that fit apart from the legs, which weigh in per call
    /// (<see cref="LegWeighted"/>).
    /// </summary>
    static AnimationStance NoLegs(in AnimationStance s) => s with { LeftLeg = 0, RightLeg = 0 };

    #endregion

    #region Animals

    /// <summary>
    /// The clip sets by ANIMAL_CHARACTER string id.
    /// </summary>
    readonly Dictionary<string, AnimalClips> _animals = new(StringComparer.Ordinal);

    /// <summary>
    /// The ANIMAL_ANIMATION record name of the lying clips: one record plays <c>gotosleep</c> once (the fall), another <c>sleep</c> in a loop.
    /// </summary>
    public const string AnimalLyingRecord = "sleeponfloor";

    /// <summary>
    /// The clip set of an ANIMAL_CHARACTER (by string id); null for anything else (people).
    /// </summary>
    public AnimalClips? Animal(string recordId) => _animals.GetValueOrDefault(recordId);

    /// <summary>
    /// Every ANIMAL_CHARACTER's clip set from its <c>animations</c> list (docs/game/animals.md "Animations"): the movement clips (non-zero <c>move
    /// speed</c>), the idles (<c>idle</c>, by <c>idle chance</c>) and its <c>sleeponfloor</c> records (the one that does not loop is the fall, the
    /// looping one the lying clip), each followed by every other <c>sleeponfloor</c> record as a fallback (engine choice: most lists name one or
    /// none; the system takes the first whose clip the skeleton has). A clip whose <c>stealth mode</c> is 1 (sneaking only) is dropped: an animal is
    /// never sneaking, so its clip filter (<c>FUN_14051d660</c>) never keeps it (Inferred for movement picks).
    /// </summary>
    void BuildAnimals(GameDatabase db)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _all.Count; i++)
            if (_all[i].Category == AnimalCategory && !_all[i].Technique) index.TryAdd(_all[i].Id, i);
        var lying = db.OfType(FcsRecordType.AnimalAnimation).Where(r => r.Name == AnimalLyingRecord && index.ContainsKey(r.StringId))
            .OrderBy(r => r.StringId, StringComparer.Ordinal).ToList();
        int[] Pick(IEnumerable<GameRecord> own, bool loop) =>
            [.. own.Concat(lying).Where(r => r.Name == AnimalLyingRecord && r.GetBool("loop") == loop).Select(r => index[r.StringId]).Distinct()];
        foreach (var a in db.OfType(FcsRecordType.AnimalCharacter))
        {
            var listed = a.GetReferences("animations").Select(r => db.Find(r.TargetStringId))
                .OfType<GameRecord>()
                .Where(r => index.ContainsKey(r.StringId) && _all[index[r.StringId]].StealthMode != 1)
                .DistinctBy(r => r.StringId)
                .ToList();
            var defs = listed.Select(r => index[r.StringId]).ToList();
            string? race = a.GetReferences("race").Where(r => r.Values.Value0 > 0).Select(r => r.TargetStringId).FirstOrDefault();
            _animals[a.StringId] = new AnimalClips(
                a.StringId, race,
                [.. defs.Where(i => _all[i].MoveSpeed != 0).OrderBy(i => _all[i].Id, StringComparer.Ordinal)],
                [.. defs.Where(i => _all[i].Idle && _all[i].MoveSpeed == 0).Select(i => (i, (float)Math.Max(_all[i].IdleChance, 0)))],
                Pick(listed, loop: false), Pick(listed, loop: true));
        }
    }

    /// <summary>
    /// The movement clips among <paramref name="candidates"/> to play at speed <paramref name="v"/>, as (index, weight): each weighted by its speed
    /// weight, normalised to sum 1 (the humanoid rule without the stance and the legs, which the animal records do not have). Nothing is added when
    /// none weighs anything.
    /// </summary>
    public void Movement(IReadOnlyList<int> candidates, float v, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in candidates)
        {
            float w = _all[i].SpeedWeight(v);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    #endregion

    #region Swimming

    /// <summary>
    /// The swimming records (category 4), by index.
    /// </summary>
    readonly int[] _swimming;

    /// <summary>
    /// Adds to <paramref name="into"/> the swimming movement clips (category 4, whole body, <c>move speed</c> set) that fit
    /// <paramref name="stance"/>, to play at speed <paramref name="v"/>, weighed and normalised like <see cref="Movement"/> (the carrying booleans
    /// pick <c>swim carry</c> and <c>swim carry left</c>). Nothing is added when none fits (a sword in the left hand).
    /// </summary>
    public void SwimMovement(float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in _swimming)
        {
            var d = _all[i];
            if (d.Idle || d.MoveSpeed == 0 || d.Area != AnimationArea.All || !d.FitsExceptLegs(stance)) continue;
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>
    /// The swimming idle (<c>swim idle</c>: the first whole-body category 4 idle that fits the stance, a carrier's carrying booleans ignored: there
    /// is none for carrying); -1 when none fits.
    /// </summary>
    public int SwimIdle(in AnimationStance stance)
    {
        var s = stance with { CarryingLeft = false, CarryingRight = false };
        foreach (int i in _swimming)
        {
            var d = _all[i];
            if (d.Idle && d.Area == AnimationArea.All && d.Fits(s)) return i;
        }
        return -1;
    }

    #endregion

    #region Crawling

    /// <summary>
    /// The grounded records (category 5), by index.
    /// </summary>
    readonly int[] _grounded;

    /// <summary>
    /// The leg health the crawl clips are weighed at: no lower than <c>crawling move</c>'s -100, so a leg past it still crawls (engine choice: the
    /// data has one crawl clip).
    /// </summary>
    const float CrawlLegFloor = -100;

    /// <summary>
    /// The crawl idles that fit a stance apart from the legs, by the stance with neutral legs.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _crawlIdles = new();

    /// <summary>
    /// Adds to <paramref name="into"/> the crawling movement clips (category 5, <c>move speed</c> set; <c>crawling move</c>) that fit
    /// <paramref name="stance"/> (prone), to play at speed <paramref name="v"/>, weighed and normalised like <see cref="SwimMovement"/>, the legs
    /// read no lower than <see cref="CrawlLegFloor"/>. Nothing is added when none fits.
    /// </summary>
    public void CrawlMovement(float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        float left = MathF.Max(stance.LeftLeg, CrawlLegFloor), right = MathF.Max(stance.RightLeg, CrawlLegFloor);
        foreach (int i in _grounded)
        {
            var d = _all[i];
            if (d.Idle || d.MoveSpeed == 0 || d.IsAction || !d.FitsExceptLegs(stance)) continue;
            float w = d.SpeedWeight(v) * d.LegWeight(left, right);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>
    /// The crawling idles that fit <paramref name="stance"/> (<c>crawl idle up</c>, and out of combat <c>crawl idle down</c>), by <c>idle chance</c>;
    /// empty when none fits.
    /// </summary>
    public IReadOnlyList<(int Index, float Weight)> CrawlIdles(in AnimationStance stance) =>
        LegWeighted(_crawlIdles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        foreach (int i in self._grounded)
        {
            var d = self._all[i];
            if (d.Idle && !d.IsAction && d.MoveSpeed == 0 && d.FitsExceptLegs(s)) list.Add((i, d.IdleWeight(s)));
        }
        return [.. list];
    }, this), stance, firstInDataOrder: true);

    #endregion

    #region Movement and hit reactions

    /// <summary>
    /// The movement candidates by body half and stance (the legs set to 100).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(AnimationArea, AnimationStance), int[]> _candidates = new();

    /// <summary>
    /// The record name of a human's side-step clip.
    /// </summary>
    public const string StrafeRecord = "strafe lower";

    /// <summary>
    /// The side-step clip's index once looked up.
    /// </summary>
    int? _strafeByName;

    /// <summary>
    /// Adds to <paramref name="into"/> the movement clips of <paramref name="area"/> (lower, or upper) to play at speed <paramref name="v"/> (units
    /// per second; negative backs off) as (index, weight): every candidate plays, weighted by its speed weight times its leg weight, the weights
    /// normalised to sum 1 (<b>Verified</b>, docs/animation.md "Choosing candidates"). Nothing is added when no candidate weighs anything.
    /// </summary>
    public void Movement(AnimationArea area, float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in CandidatesFor(area, stance))
        {
            var d = _all[i];
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>
    /// The hit reactions (stumbles) a hit to body part <paramref name="part"/> (a LOCATIONAL_DAMAGE string id) may play, with their weights
    /// (<b>Verified</b>, docs/animation.md "Hit reactions"): the clips of the stance's <see cref="AnimationStance.Category"/> listing the part in
    /// <c>stumbles</c> that fit the stance, whose <c>big stumble</c> equals <paramref name="big"/> and whose <c>stumble from</c> is the side (0
    /// front, 1 rear), weighing <c>chance</c> / 100; the one already playing (<paramref name="playing"/>) weighs a quarter of that. Every base-game
    /// stumble has <c>chance</c> 0, so when all weigh 0 they are equally likely (<b>engine choice</b>).
    /// </summary>
    public void Stumbles(string part, bool big, int side, in AnimationStance stance, int playing, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        for (int i = 0; i < _all.Count; i++)
        {
            var d = _all[i];
            bool matches = (d.StumbleCategory ?? d.Category) == stance.Category && d.Stumbles.Count != 0 && d.BigStumble == big
                && d.StumbleFrom == side && d.Stumbles.Contains(part) && d.FitsExceptLegs(stance);
            if (!matches) continue;
            float w = d.Chance / 100f * (i == playing ? 0.25f : 1);
            into.Add((i, w));
            total += w;
        }
        if (total <= 0)
            for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Index == playing ? 0.25f : 1f);
    }

    /// <summary>
    /// The side-step clip of a human fighter (<c>FUN_14051fc60</c>, <b>Verified</b>): the record NAMED <c>strafe lower</c> from the race's by-name
    /// map (the first lookup cached for every later character), with no stance fit test; -1 when the data has none. The animals' side step is their
    /// own <c>strafe anim</c> list.
    /// </summary>
    public int Strafe(in AnimationStance stance) => _strafeByName ??= IndexOf(StrafeRecord);

    /// <summary>
    /// The movement clips of a body half (<b>Verified</b>, docs/animation.md "Movement"): the lower list is every clip with a non-zero <c>move
    /// speed</c> whose layer is not <c>upper</c> or <c>overlay</c>, the upper list the <c>upper</c> ones; idles and actions are not movement. The
    /// stance filters them, except for the legs (weighed per call).
    /// </summary>
    int[] CandidatesFor(AnimationArea area, in AnimationStance stance) =>
        _candidates.GetOrAdd((area, stance with { LeftLeg = 100, RightLeg = 100 }), static (key, self) =>
    {
        var list = new List<int>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category != 0 || d.MoveSpeed == 0 || d.Idle || d.IsAction || d.Area == AnimationArea.Overlay)
                continue;
            if ((key.Item1 == AnimationArea.Upper) != (d.Area == AnimationArea.Upper)) continue;
            if (d.FitsExceptLegs(key.Item2)) list.Add(i);
        }
        return [.. list.OrderBy(i => self._all[i].Id, StringComparer.Ordinal)];
    }, this);

    #endregion

    #region Idles

    /// <summary>
    /// The whole-body idles that fit a stance apart from the legs, by the stance with neutral legs.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _idles = new();

    /// <summary>
    /// The combat idles that fit a stance apart from the legs, by the stance with neutral legs.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _combatIdles = new();

    /// <summary>
    /// The carry poses by canonical stance.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, int> _carryIdles = new();

    /// <summary>
    /// Entries in the movement and idle caches (it stays small however the legs' health varies; for tests).
    /// </summary>
    public int CachedChoices => _candidates.Count + _idles.Count;

    /// <summary>
    /// The standing animations valid for the stance: <c>idle</c> clips of the whole body (not actions, not carry overlays) with their <c>idle
    /// chance</c>.
    /// </summary>
    public IReadOnlyList<(int Index, float Weight)> Idles(in AnimationStance stance) =>
        LegWeighted(_idles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.All && d.FitsExceptLegs(s))
                list.Add((i, d.IdleWeight(s)));
        }
        return list.ToArray();
    }, this), stance);

    /// <summary>
    /// The idles of a character standing in combat: the upper-body <c>idle</c> clips (the guards, <c>MA idle1</c>) that fit the stance: combat mode,
    /// arm check and weapon kinds (<see cref="AnimationDefinition.ArmsFit"/>, <see cref="AnimationDefinition.KindFits"/>: the guards want exactly one
    /// hand armed), by <c>idle chance</c>. <b>Observed</b> (docs/animation.md "Combat upper body"): the original picks from the idle list by weapon
    /// type and carrying. Without any, the whole-body idles that fit apart from the combat mode.
    /// </summary>
    public IReadOnlyList<(int Index, float Weight)> CombatIdles(in AnimationStance stance) =>
        LegWeighted(_combatIdles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.Upper && d.CombatMode != 0 && d.FitsExceptLegs(s))
                list.Add((i, d.IdleWeight(s)));
        }
        if (list.Count == 0)
            for (int i = 0; i < self._all.Count; i++)
            {
                var d = self._all[i];
                if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.All
                    && d.FitsExceptLegs(s with { Combat = d.CombatMode == 1 }))
                    list.Add((i, d.IdleWeight(s)));
            }
        return list.ToArray();
    }, this), stance);

    /// <summary>
    /// The guard a fighter's upper body holds (<b>Verified</b>, the combat pick <c>AnimationClassHuman</c> slot 28 through <c>FUN_14051d660</c>): of
    /// <see cref="CombatIdles"/>, the one with the highest candidate weight (speed weight x leg weight) at speed <paramref name="v"/>, the first in
    /// the original's list order on a tie (<see cref="DataOrder"/>), chosen again every tick; -1 when there is none.
    /// </summary>
    public int CombatGuard(in AnimationStance stance, float v)
    {
        int best = -1;
        float bestWeight = float.NegativeInfinity;
        foreach (var (i, _) in CombatIdles(stance))
        {
            var d = _all[i];
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (w > bestWeight || best >= 0 && w == bestWeight && DataOrder(i) < DataOrder(best))
                (best, bestWeight) = (i, w);
        }
        return best;
    }

    /// <summary>
    /// The held arm pose of a carrier standing (or of a carried body): the first idle overlay or upper clip, in record order, whose carrying booleans
    /// are set and fit the stance (<c>stand carrying left</c> in the base game); -1 when none. Played over a whole-body idle of the same stance
    /// without the carrying (engine choice).
    /// </summary>
    public int CarryIdle(in AnimationStance stance) => _carryIdles.GetOrAdd(Canonical(stance), static (s, self) =>
    {
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            bool carrying = d.CarryingLeft || d.CarryingRight || d.BeingCarried;
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area is AnimationArea.Overlay or AnimationArea.Upper && carrying && d.Fits(s))
                return i;
        }
        return -1;
    }, this);

    /// <summary>
    /// An idle draw's weights (<c>FUN_14051ec80</c>, <b>Verified</b>): <c>idle chance</c> x the leg ramp
    /// (<see cref="AnimationDefinition.LegWeight"/>, the same as a movement clip's) of the more hurt leg, only candidates above 0 kept. With
    /// <paramref name="firstInDataOrder"/> a draw that keeps none takes the first in data order (the draw never comes back empty; the crawl idles,
    /// whose -999 minimum reads as -101).
    /// </summary>
    IReadOnlyList<(int Index, float Weight)> LegWeighted((int Index, float Weight)[] fits, in AnimationStance s, bool firstInDataOrder = false)
    {
        var list = new List<(int, float)>(fits.Length);
        foreach (var (i, chance) in fits)
            if (_all[i].LegWeight(s.LeftLeg, s.RightLeg) is var leg and > 0) list.Add((i, chance * leg));
        if (list.Count == 0 && firstInDataOrder && fits.Length > 0)
        {
            var first = fits.OrderBy(f => DataOrder(f.Index)).First();
            list.Add(first);
        }
        return list;
    }

    #endregion
}

/// <summary>
/// The clips an ANIMAL_CHARACTER plays, as indices into its <see cref="AnimationLibrary"/> (<see cref="AnimationLibrary.Animal"/>): from its own
/// <c>animations</c> list, the movement clips, the idles with their weights, and the fall and lying candidates in order (its own first).
/// <paramref name="RaceId"/> picks the skeleton's clip lengths (<see cref="AnimationLengths.For"/>). Its techniques are the creature techniques of
/// its weapon's category, which the combat system chooses.
/// </summary>
public sealed record AnimalClips(string RecordId, string? RaceId, int[] Movement, (int Index, float Weight)[] Idles, int[] Fall, int[] Lying);
