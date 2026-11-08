using System.Numerics;
using Meitou.Data;
using Meitou.Data.Particles;
using Meitou.Data.Save;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Particles;
using Meitou.Simulation.Saving;
using EffectType = Meitou.Data.Particles.EffectType;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;

namespace Meitou.Game.Title;

/// <summary>The title screen's world: its staged characters, camera and light, and the Continue save's leader drawn in its saved look.</summary>
sealed class TitleScreen(GameHost host)
{
    TitleComposition? titleComposition;
    internal TitleBattle? titleBattle;
    Meitou.Data.Characters.CharacterAppearance[] titleAppearances = [];
    internal bool TitleCharactersReady => host.ActiveCharacters is not { } characters ||
        titleAppearances.Length > 0 && titleAppearances.All(characters.IsPrepared) && !characters.ContentPending;
    internal bool TitleSceneReady => TitleCharactersReady && (host.gpu is null || host.gpu.Streamer is not { Idle: false } &&
        !(host.gpu.Objects?.Pending > 0) && !(host.gpu.Foliage?.Pending > 0));
    internal Func<ParticleRenderer>? titleParticleFactory;
    ParticleRenderer? titleParticles;
    IReadOnlyList<WeatherEffectEntry>? titleDust;
    float? gameplayLens;
    /// <summary>The gameplay character source, set aside while the title shows its staged actors.</summary>
    Action<CharacterDrawList>? gameplayCharacters;
    internal bool TitleVisible => host.player.TitleOpen && host.player.UiMode == UiMode.Meitou;
    internal float TitleHour => host.scene.Clock.Sunrise + 0.75f;
    /// <summary>The moons' fill light, and the shafts' air layer (a multiple of the usual) on the title: thick morning air so the low sun casts strong rays.</summary>
    const float TitlePlanetFill = 0.8f, TitleShaftAir = 2, TitleClouds = 0.38f;
    /// <summary>The title's white balance: keeps the orange of the early hour with the sun a little higher than that hour would put it.</summary>
    static readonly System.Numerics.Vector3 TitleBalance = new(1.1f, 0.98f, 0.88f);
    /// <summary>The wanderer's fill light on the title: warm sky light so his back, turned to the low sun, is not a flat silhouette.</summary>
    static readonly System.Numerics.Vector3 TitleFill = new(0.064f, 0.052f, 0.04f);

    void EnsureTitleScene()
    {
        if (titleComposition is not null || host.scene.Database is not { } db) return;
        var saved = TitleSaveAppearance(db, out var stats, out float hunger);
        // Its body from its saved stats and hunger, as the world publishes it (CharacterSnapshot.Build; FUN_140531820 skips robots).
        var build = saved is { Race: var race } && stats is not null && race.GetBool("is robot") != true
            ? Meitou.Data.Characters.CharacterShape.BuildFromStats(saved.Female, stats, static (stats, s) => stats[s], hunger) : (Meitou.Data.Characters.BodyBuild?)null;
        titleComposition = new(host.install, db, host.scene.GroundAt, host.sandbox.SandboxRecord(db), saved) { SavedBuild = build };
        if (host.popData is { } data && host.animationLengths is { } lengths && host.animationLibrary is { } library)
            titleBattle = TitleBattle.Create(data, host.scene.GroundAt, titleComposition.Fights, lengths, library);
        if (host.ActiveCharacters is { } characters)
        {
            var list = new CharacterDrawList();
            FillTitleCharacters(list);
            titleAppearances = [.. list.Items.Select(c => c.Appearance)];
            foreach (var appearance in titleAppearances) characters.Prefetch(appearance);
        }
    }

    /// <summary>The first-created character in the player's first squad in the save Continue will load, drawn with its saved look and worn kit.</summary>
    /// <param name="stats">Its saved stats (null: unread).</param>
    /// <param name="hunger">Its saved hunger (0 to 3).</param>
    Meitou.Data.Characters.CharacterAppearance? TitleSaveAppearance(GameDatabase db, out Meitou.Simulation.Bodies.CharacterStats? stats, out float hunger)
    {
        (stats, hunger) = (null, Meitou.Simulation.Bodies.MedicalState.MaxHunger);
        try
        {
            SaveGame? save = host.StartSave;
            if (save is null)
            {
                var slots = SaveCatalog.Scan(QuickSave.DefaultRoot, host.install.SaveDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save"));
                if (slots.FirstOrDefault() is { } slot) save = SaveGame.Load(slot.Folder);
            }
            if (save?.PlayerFaction is not { } faction || host.popData?.Appearances is not { } appearances) return null;
            var platoons = save.Platoons.Where(p => p.FactionId == faction.Id && p.File is not null && p.Characters.Count > 0)
                .OrderBy(p => p.Record.Ints.GetValueOrDefault("squad index", int.MaxValue)).ToArray();
            var candidates = platoons.SelectMany(p => p.Characters
                .Where(c => db.Find(c.RecordId)?.Type == Meitou.Data.Fcs.FcsRecordType.CHARACTER && c.Medical?.Bools.GetValueOrDefault("dead") != true)
                .Select(c => (Platoon: p, Character: c))).ToArray();
            if (candidates.Length == 0) return null;
            var startingRecords = StartingCharacterRecords(db, platoons[0].SquadTemplate);
            var startingCandidates = candidates.Where(c => startingRecords.Contains(c.Character.RecordId)).ToArray();
            // Player-hired characters are written into the player's faction too. Restrict to the start roster when it resolves,
            // including when every starting character has died, so a recruit never replaces the protagonist.
            if (startingRecords.Count > 0) candidates = startingCandidates;
            if (candidates.Length == 0) return null;
            // Squad and character slot order makes a dead first member fall through to the next living one.
            var character = candidates.OrderBy(c => c.Platoon.Record.Ints.GetValueOrDefault("squad index", int.MaxValue))
                .ThenBy(c => c.Character.Handle.I).First().Character;
            var platoon = character.Platoon;
            if (character is null) return null;
            int serial = character.Handle.S & 0x7FFFFFFF;
            if (character.Stats is { } savedStats) stats = Meitou.Simulation.Bodies.CharacterStats.ReadSave(savedStats.Floats);
            if (character.Medical?.Floats.TryGetValue("hung", out float saved) == true) hunger = saved;
            var items = character.Inventory is { } inventory && platoon?.File is { } file ? SaveInventory.Read(file, inventory) : [];
            if (items.Count > 0 && SavedKit.LoadoutOf(character.Appearance, items, db, serial) is { } exactKit &&
                appearances.Create(character.RecordId, WithSavedLimbs(exactKit, character, db)) is { } exact) return exact;
            var rolled = appearances.Create(character.RecordId, character.OwnerFactionId.Length > 0 ? character.OwnerFactionId : null, serial);
            if (rolled?.Loadout is not { } generated) return null;
            var worn = SavedKit.Wear(generated, character.Appearance, db) ?? SavedKit.Copy(generated);
            return appearances.Create(character.RecordId, WithSavedLimbs(worn, character, db)) ?? rolled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or Meitou.Data.Fcs.FcsFormatException)
        {
            return null;
        }
    }

    /// <summary><paramref name="kit"/> with the character's saved stumps and prosthetics, as the world loads them (SavedLimbs).</summary>
    static Meitou.Data.Characters.Loadout WithSavedLimbs(Meitou.Data.Characters.Loadout kit, SaveCharacter character, GameDatabase db)
    {
        if (character.Medical is { } medical && character.Platoon.File is { } file) SavedKit.SetLimbs(kit, file, medical, db);
        return kit;
    }

    static HashSet<string> StartingCharacterRecords(GameDatabase db, string startOrTemplateId)
    {
        var characters = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id)
        {
            if (id.Length == 0 || !visited.Add(id) || db.Find(id) is not { } record) return;
            if (record.Type is Meitou.Data.Fcs.FcsRecordType.CHARACTER or Meitou.Data.Fcs.FcsRecordType.ANIMAL_CHARACTER)
            {
                characters.Add(id);
                return;
            }
            if (record.Type == Meitou.Data.Fcs.FcsRecordType.NEW_GAME_STARTOFF)
            {
                foreach (var member in Meitou.Data.Gameplay.NewGameStart.From(record).Squad) Add(member.Id);
                return;
            }
            if (record.Type is Meitou.Data.Fcs.FcsRecordType.SQUAD_TEMPLATE or Meitou.Data.Fcs.FcsRecordType.UNIQUE_SQUAD_TEMPLATE)
                foreach (var start in Meitou.Data.Gameplay.NewGameStart.LoadAll(db).Where(s => s.Squad.Any(m => m.Id == id)))
                    foreach (var member in start.Squad) Add(member.Id);
            if (record.Type is not (Meitou.Data.Fcs.FcsRecordType.SQUAD_TEMPLATE or Meitou.Data.Fcs.FcsRecordType.UNIQUE_SQUAD_TEMPLATE)) return;
            var template = Meitou.Data.Gameplay.SquadTemplate.From(record);
            if (template.Leader is { } leader) Add(leader.Id);
            foreach (var member in template.Squad.Concat(template.Squad2).Concat(template.ChooseFrom).Concat(template.Animals).Concat(template.Animals2)) Add(member.Id);
        }
        Add(startOrTemplateId);
        return characters;
    }

    void FillTitleCharacters(CharacterDrawList list)
    {
        titleComposition?.Fill(list);
        titleBattle?.Fill(list);
    }

    /// <summary>
    /// The title's own view of the real world (<see cref="TitleComposition.Spot"/>), independent of the gameplay camera rig; the world's renderers
    /// stream round it as round any camera. While the title shows, only its staged actors are drawn.
    /// </summary>
    internal void ApplyTitleCamera()
    {
        if (!TitleVisible)
        {
            if (gameplayLens is { } lens) { host.camera.FieldOfView = lens; host.camera.MaxNear = 200; gameplayLens = null; }
            if (gameplayCharacters is { } source && host.ActiveCharacters is { } restore) { restore.Source = source; gameplayCharacters = null; }
            if (host.ActiveFoliage is { } plants && titleComposition is { } shown && plants.Shift == shown.Ruins) plants.Shift = null;
            if (host.ActiveObjects is { } things && titleComposition is { } staged && things.Staged == staged.Staged) things.Staged = null;
            return;
        }
        EnsureTitleScene();
        if (titleComposition is not { } title) return;
        gameplayLens ??= host.camera.FieldOfView;
        host.player.titleMenu.TitleLogoSource ??= TitleLogo.Load;
        if (host.ActiveCharacters is { } characters && gameplayCharacters is null)
        {
            gameplayCharacters = characters.Source;
            characters.Source = FillTitleCharacters;

            // The world's characters beyond reach are kept across frames and only the gameplay source empties them: left in, the title's view
            // waited forever for looks it never builds (out of its reach). The gameplay source fills them again when it returns.
            characters.Draws.BeginDistant();
            host.distantFor = default;
        }
        if (host.ActiveFoliage is { } foliage && foliage.Shift != title.Ruins) foliage.Shift = title.Ruins;
        if (host.ActiveObjects is { } objects && objects.Staged != title.Staged) objects.Staged = title.Staged;
        TitleComposition.Place(host.camera);
    }

    internal void DrawTitleWorld(int width, int height)
    {
        EnsureTitleScene();
        if (titleComposition is not { } title) return;
        double seconds = host.realTime.Elapsed.TotalSeconds;
        PrepareTitleDust(seconds);
        // Clear sky with a few clouds whatever the region's weather, no placed fog volumes, the sun turned into the right of the view, the moons lit
        // full face so they read by day, and thicker morning air so the hill the sun peeks over casts rays through it.
        var sky = host.gpu.Sky;
        var (weather, coverage, regions, clock, wind) = (sky.Weather, sky.CloudCoverage, host.gpu.Weather, host.scene.Clock, sky.CloudWind);
        float hour = host.presentationHour ?? TitleHour;
        // Fade the staged fill with the sun, reaching zero at the horizon.
        float fill = Math.Clamp(clock.SunDirection(hour).Y / MathF.Max(clock.SunDirection(TitleHour).Y, 1e-3f), 0, 1);
        fill = fill * fill * (3 - 2 * fill);
        sky.Weather = SkyWeather.Default;
        sky.CloudCoverage = TitleClouds;
        sky.CloudWind = TitleComposition.CloudWind(host.camera);
        sky.PlanetTurn = TitleComposition.MoonTurn(host.camera);
        sky.PlanetFill = TitlePlanetFill * fill;
        if (host.gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        if (host.gpu.Characters is { } lit) lit.Fill = TitleFill * fill;
        var shafts = host.gpu.Post?.Options;
        var (air, strength, balance) = shafts is null ? (0f, 0f, System.Numerics.Vector3.One) : (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance);
        if (shafts is not null) (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance) = (TitleShaftAir, 1, TitleBalance);
        host.gpu.Weather = null;
        host.scene.Clock = clock with { SunTurn = TitleComposition.SunTurn(clock, hour, host.camera) };
        // A separate particle scene keeps the menu's dust out of gameplay and excludes the map's effect placers.
        var worldParticles = host.gpu.Particles;
        float density = host.gpu.ParticleDensity;
        bool weatherParticles = host.gpu.WeatherParticles;
        host.gpu.Particles = titleParticles;
        host.gpu.ParticleDensity = 0.3f;
        host.gpu.WeatherParticles = true;
        if (host.gpu.Foliage is { } foliage && host.o.Screenshot is null) foliage.SwaySeconds = seconds;
        try { WorldFrame.Draw(host.gpu, host.scene, host.camera, host.render, width, height, hour, (float)seconds / 600f, host.o.FogDistance); }
        finally
        {
            (host.gpu.Particles, host.gpu.ParticleDensity, host.gpu.WeatherParticles) = (worldParticles, density, weatherParticles);
            (sky.Weather, sky.CloudCoverage, host.gpu.Weather, host.scene.Clock, sky.PlanetFill, sky.CloudWind, sky.PlanetTurn) = (weather, coverage, regions, clock, 0, wind, System.Numerics.Quaternion.Identity);
            if (shafts is not null) (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance) = (air, strength, balance);
            if (host.gpu.FogVolumes is { } restoreFog) restoreFog.HidePlaced = false;
            if (host.gpu.Characters is { } unlit) unlit.Fill = default;
        }
    }

    void PrepareTitleDust(double seconds)
    {
        if (titleParticles is null && titleParticleFactory is { } create)
        {
            titleParticles = create();
            titleParticles.PrewarmSeconds = 5;
            titleParticles.Seed = 173;
            titleParticles.World.Density = 0.3f;
            titleDust = titleParticles.Library.FindSystem("kenshi_weather_dust1") is null ? [] :
                [new(new EffectRecord("Title dust", EffectType.Camera, "kenshi_weather_dust1", true, false, 1,
                    0, 0, Vector3.One, 0, false, 4000, 0, 0, 0, 0), 1, 0, 0)];
        }
        if (titleParticles is not { } dust) return;
        var left = -Vector3.Normalize(Vector3.Cross(host.camera.Forward, Vector3.UnitY));
        float gust = 0.5f + 0.5f * MathF.Sin((float)(seconds % 120) * MathF.Tau / 24);
        dust.SetWeather(new WeatherEffectInput { Effects = titleDust!, Wind = new Vector2(left.X, left.Z) * (35 + 20 * gust) });
        // Warm once before setting the tint; Draw advances subsequent frames on the menu's real-time clock.
        if (dust.Groups.Count == 0) dust.Update(seconds, host.camera);
        float fade = Math.Clamp((host.camera.Pitch + 0.65f) / 0.45f, 0, 1);
        foreach (var group in dust.Groups)
            foreach (var unit in group.Units)
                unit.Tint = new Vector3(1, 0.86f, 0.68f) * ((0.4f + 0.25f * gust) * fade);
    }

    internal void DisposeTitleScene()
    {
        titleParticles?.Dispose();
        titleParticles = null;
        titleBattle?.Dispose();
        titleBattle = null;
        titleAppearances = [];
        titleComposition = null;
    }
}
