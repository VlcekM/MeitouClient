using System.Numerics;
using Meitou.Data.Save;
using Meitou.Game.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// The blood on weapons across a save (docs/game/blood-decals.md "On weapons and gear"): written beside the save as <see cref="SaveWeaponBlood"/>, keyed by the
/// characters' save handles (the renderer's draw keys are new for every world), and put back on the characters of the loaded world, those not met yet (their zone not
/// loaded) as they appear.
/// </summary>
sealed partial class BloodDecals
{
    /// <summary>
    /// The entries of the loaded save that no character of the world has matched yet; carried into the next save so a character far away keeps its blood.
    /// </summary>
    List<SaveWeaponBlood.Entry> _pendingWeapons = [];
    IReadOnlyDictionary<CharacterId, Hand> _writtenHandles = new Dictionary<CharacterId, Hand>();
    int _pendingWait;

    /// <summary>
    /// The handle each character was written with by the capture that is just finishing (<see cref="SaveCaptureOptions.HandlesWritten"/>).
    /// </summary>
    internal void NoteHandles(IReadOnlyDictionary<CharacterId, Hand> handles) => _writtenHandles = new Dictionary<CharacterId, Hand>(handles);

    /// <summary>
    /// The weapon blood into <paramref name="game"/> (after its capture): every character's now, plus the entries not matched yet. With the feature off the
    /// loaded save's file is carried on as it was.
    /// </summary>
    internal void WriteWeaponBlood(SaveGame game)
    {
        if (!GearBlood.Enabled || host.Gpu?.Characters is not { } crowd)
        {
            game.WeaponBlood = host.Saves.LoadedSave?.Source.WeaponBlood;
            return;
        }
        long epoch = (long)host.Saves.WorldEpoch << 56;
        var entries = new List<SaveWeaponBlood.Entry>();
        foreach (var (key, amount, wet, colour, back) in crowd.WeaponBloodNow())
        {
            if ((key & ~((1L << 56) - 1)) != epoch) continue;   // an earlier world's
            long v = (key & ((1L << 56) - 1)) - 1;
            var id = new CharacterId((int)(v >> 32), (int)(uint)v);
            if (_writtenHandles.TryGetValue(id, out var hand) && !hand.IsNull)
                entries.Add(new SaveWeaponBlood.Entry(hand, amount, wet, colour.X, colour.Y, colour.Z, back));
        }
        entries.AddRange(_pendingWeapons);
        game.WeaponBlood = entries.Count > 0 ? SaveWeaponBlood.Write(entries) : null;
    }

    /// <summary>
    /// The weapon blood of the loaded save (a file missing or stale reads as none: clean weapons), put on the characters now in the world.
    /// </summary>
    internal void ReadWeaponBlood(SaveGame save)
    {
        _pendingWeapons = SaveWeaponBlood.Read(save.WeaponBlood);
        MatchPendingWeapons();
    }

    /// <summary>
    /// Once a frame: a character that appeared since (its zone loaded) takes the blood its save handle names, looked for every couple of seconds.
    /// </summary>
    void RetryPendingWeapons()
    {
        if (_pendingWeapons.Count == 0 || _pendingWait++ < 120) return;
        _pendingWait = 0;
        MatchPendingWeapons();
    }

    void MatchPendingWeapons()
    {
        if (_pendingWeapons.Count == 0 || !GearBlood.Enabled || host.Gpu?.Characters is not { } crowd) return;
        var table = host.Session.World.Characters;
        var wanted = new Dictionary<Hand, SaveWeaponBlood.Entry>();
        foreach (var e in _pendingWeapons) wanted[e.Hand] = e;
        for (int slot = 0; slot < table.HighWater && wanted.Count > 0; slot++)
        {
            if (!table.Next[slot].Alive || table.Cold(slot)?.Save?.Source.Handle is not { IsNull: false } hand || !wanted.Remove(hand, out var e)) continue;
            crowd.RestoreWeaponBlood(host.DrawKey(table.IdOf(slot)), e.Amount, e.Wet, new Vector3(e.R, e.G, e.B), e.Back);
        }
        _pendingWeapons = [.. wanted.Values];
    }
}
