using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The <c>gearblood</c> switch (Meitou, optional, off): blood on worn items and on weapons that wound (docs/game/blood-decals.md "On weapons and
/// gear").
/// </summary>
static class GearBlood
{
    /// <summary>
    /// The switch is on.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Game seconds a struck weapon takes to dry from wet blood to dried (darker, matte).
    /// </summary>
    public const float DrySeconds = 150;

    /// <summary>
    /// Game seconds a dry coat takes to flake off alone (the amount falls by (1 - wet) / this a second).
    /// </summary>
    public const float FlakeSeconds = 1500;

    /// <summary>
    /// The seconds submerged to the weapon's hand (the water over the feet times 1.1 above <see cref="WaterReach"/>) that wash it clean.
    /// </summary>
    public const float WashSeconds = 8;

    /// <summary>
    /// The seconds of full rain that wash a weapon clean.
    /// </summary>
    public const float RainSeconds = 300;

    /// <summary>
    /// The water depth over the feet (times 1.1) above which the weapon's hand is submerged.
    /// </summary>
    public const float WaterReach = 8;

    /// <summary>
    /// What a hit adds to a weapon's amount: a floor plus the splash's size (0.05 to 0.5).
    /// </summary>
    public static float Gain(float splash) => 0.15f + splash;

    /// <summary>
    /// The dried colour: blood darkens and browns as it dries.
    /// </summary>
    public static Vector3 Dried(Vector3 fresh, float wet) => fresh * Vector3.Lerp(new Vector3(0.45f, 0.36f, 0.32f), Vector3.One, wet);

    /// <summary>
    /// The colour as a whole number of 24 bits (a float holds it exactly).
    /// </summary>
    public static float PackColour(Vector3 c)
    {
        static uint B(float v) => (uint)Math.Clamp(MathF.Round(v * 255), 0, 255);

        return B(c.X) << 16 | B(c.Y) << 8 | B(c.Z);
    }

    /// <summary>
    /// The amount and the wetness (0..1) as two bytes in one whole number.
    /// </summary>
    public static float PackAmounts(float amount, float wet) =>
        (uint)Math.Clamp(MathF.Round(amount * 255), 0, 255) | (uint)Math.Clamp(MathF.Round(wet * 255), 0, 255) << 8;
}

internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// What a character's weapon has on it: the amount of blood (0..1), how wet it still is (1 just struck), the colour of the victim's blood and the
    /// game second it was last stepped. Kept by the character's draw key, not in <see cref="CharState"/>, which is forgotten a second after the
    /// character leaves the screen. Saved beside the save (<see cref="Meitou.Data.Save.SaveWeaponBlood"/>, by the host: <see cref="WeaponBloodNow"/>,
    /// <see cref="RestoreWeaponBlood"/>).
    /// </summary>
    sealed class WeaponBlood
    {
        /// <summary>
        /// The amount of blood, 0..1.
        /// </summary>
        public float Amount;

        /// <summary>
        /// How wet the blood still is, 0..1 (1 just struck).
        /// </summary>
        public float Wet;

        /// <summary>
        /// The colour of the victims' blood, mixed by their shares.
        /// </summary>
        public Vector3 Colour;

        /// <summary>
        /// The game second it was last stepped.
        /// </summary>
        public double At;

        /// <summary>
        /// The bloodied weapon is the one on the back.
        /// </summary>
        public bool Back;

        /// <summary>
        /// Put back from a save: the clock it was last stepped on is the old world's, so the first step only takes the new world's time as its start.
        /// </summary>
        public bool Restored;
    }

    /// <summary>
    /// The weapons' blood by the character's draw key.
    /// </summary>
    readonly Dictionary<long, WeaponBlood> _weaponBlood = [];

    /// <summary>
    /// The weather's surface wetness, 0..1 (what rain washes a weapon with).
    /// </summary>
    public Func<float>? RainWetness { get; set; }

    /// <summary>
    /// A melee hit with the weapon of the character <paramref name="key"/> drew blood: the weapon takes more of it, wet and in the victim's colour
    /// (<paramref name="colour"/> 0xRRGGBB; <paramref name="splash"/> the hit's size, 0.05 to 0.5). Render thread.
    /// </summary>
    public void WeaponHit(long key, uint colour, float splash)
    {
        if (!GearBlood.Enabled) return;

        if (!_weaponBlood.TryGetValue(key, out var w)) _weaponBlood[key] = w = new WeaponBlood();
        double now = SoakClock?.Invoke() ?? _poseSeconds;
        StepWeaponBlood(w, now, false);
        var fresh = new Vector3((colour >> 16 & 255) / 255f, (colour >> 8 & 255) / 255f, (colour & 255) / 255f);
        float gain = GearBlood.Gain(splash);

        // A struck coat takes the new blood's colour by its share of the whole, the old blood dried or not.
        w.Colour = w.Amount <= 0.02f ? fresh : Vector3.Lerp(w.Colour, fresh, gain / (w.Amount + gain));
        w.Amount = MathF.Min(1, w.Amount + gain);
        w.Wet = 1;
        w.Back = _states.TryGetValue(key, out var s) && s.BackDrawn;
    }

    /// <summary>
    /// Ages a weapon's blood by the game seconds since it was last stepped: it dries, flakes off, and washes off in water over the hand and in rain.
    /// </summary>
    void StepWeaponBlood(WeaponBlood w, double now, bool submerged)
    {
        if (w.Restored)
        {
            (w.At, w.Restored) = (now, false);
            return;
        }

        float dt = (float)Math.Max(now - w.At, 0);
        w.At = now;
        if (dt <= 0 || w.Amount <= 0) return;

        float rain = Math.Clamp(RainWetness?.Invoke() ?? 0, 0, 1);
        int steps = Math.Clamp((int)MathF.Ceiling(dt / 4), 1, 64);
        float h = dt / steps;
        for (int i = 0; i < steps && w.Amount > 0; i++)
        {
            w.Wet = MathF.Max(0, w.Wet - h / GearBlood.DrySeconds);
            float rate = (1 - w.Wet) / GearBlood.FlakeSeconds + (submerged ? 1f : 0f) / GearBlood.WashSeconds + rain / GearBlood.RainSeconds;
            w.Amount = MathF.Max(0, w.Amount - rate * h);
        }
    }

    /// <summary>
    /// The character's weapon blood for its instances this frame (<see cref="Live.GearColour"/>, <see cref="Live.GearAmounts"/>).
    /// </summary>
    void WeaponBloodOf(ref Live l)
    {
        l.GearColour = l.GearAmounts = 0;
        if (!GearBlood.Enabled || !_weaponBlood.TryGetValue(l.Key, out var w)) return;

        double now = SoakClock?.Invoke() ?? _poseSeconds;
        float depth = 1.1f * (Meitou.Data.World.WorldWater.Height - l.World.Translation.Y);
        StepWeaponBlood(w, now, depth > GearBlood.WaterReach);
        if (w.Amount <= 0.004f)
        {
            _weaponBlood.Remove(l.Key);
            return;
        }

        l.GearColour = GearBlood.PackColour(GearBlood.Dried(w.Colour, w.Wet));
        l.GearAmounts = GearBlood.PackAmounts(w.Amount, w.Wet);
        l.GearBack = w.Back;
    }

    /// <summary>
    /// The weapon blood of every character, aged to now: the draw key (the host's DrawKey), amount, wetness, the victim's colour and
    /// whether the weapon on the back is the one.
    /// </summary>
    public List<(long Key, float Amount, float Wet, Vector3 Colour, bool Back)> WeaponBloodNow()
    {
        var list = new List<(long, float, float, Vector3, bool)>();
        double now = SoakClock?.Invoke() ?? _poseSeconds;
        foreach (var (key, w) in _weaponBlood)
        {
            StepWeaponBlood(w, now, false);
            if (w.Amount > 0.004f) list.Add((key, w.Amount, w.Wet, w.Colour, w.Back));
        }

        return list;
    }

    /// <summary>
    /// Puts back the blood a save held on the weapon of the character <paramref name="key"/> (a loaded game). Ageing goes on from the first step in
    /// the new world.
    /// </summary>
    public void RestoreWeaponBlood(long key, float amount, float wet, Vector3 colour, bool back) =>
        _weaponBlood[key] = new WeaponBlood { Amount = amount, Wet = wet, Colour = colour, Back = back, Restored = true };

    /// <summary>
    /// The matrix an instance of <paramref name="part"/> is drawn with and its extra flags: a worn item that takes blood gets the body's amounts in
    /// the matrix's w (as the body does), a weapon's blade its colour and amounts.
    /// </summary>
    static Matrix4x4 GearModel(AssetPart part, in Live l, Matrix4x4 model, ref uint flags)
    {
        if (!GearBlood.Enabled || !part.Material.GearBlood) return model;
        if (part.Shared) return WithBlood(model, l.Blood);

        bool back = part.Shown is WeaponShown.BackDrawn or WeaponShown.BackSheathed;
        if (l.GearAmounts == 0 || part.Shown != WeaponShown.Always && back != l.GearBack) return model;

        flags |= CharacterShaders.WeaponBlood;
        model.M14 = l.GearColour;
        model.M24 = l.GearAmounts;
        return model;
    }
}
