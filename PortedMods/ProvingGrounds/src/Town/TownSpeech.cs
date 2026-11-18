using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Town;

/// <summary>M8's one speech helper (shared brief "Speech"; CAP 16): a town character's line through <c>PgEngine.Bark</c> (one bubble per speaker,
/// a new one replaces the old) and, while the test verbs are armed, the TEST-ONLY say line
/// <c>PG-TOWN {"event":"say","by":"&lt;announcer|crowd|limbshop&gt;","tick":...,"id":...,"text":...}</c> (the pattern of ProvingGroundsSystem's
/// IngressSaid / PrisonerSaid), so scenarios check every text byte for byte. Sim thread, serial steps.</summary>
static class TownSpeech
{
    /// <summary><paramref name="c"/> says <paramref name="text"/> (the C++ <c>Character::say</c>); <paramref name="by"/> names the speaker's role in
    /// the test line. False (nothing said, no line) for a stale id.</summary>
    internal static bool Say(World world, CharacterId c, string by, string text)
    {
        if (!PgEngine.Bark(world, c, text)) return false;
        if (Spikes.SpikePatches.Armed)
            PgPerf.TestLine("PG-TOWN {\"event\":\"say\",\"by\":" + PGLog.Quote(by) + ",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
                + ",\"id\":" + PGLog.Quote(CharacterIdText.Format(c)) + ",\"text\":" + PGLog.Quote(text) + "}");
        return true;
    }
}
