// Port of upstream/ProvingGrounds/plugin/src/PGConfig.cpp (Proving Grounds by Shutori, GPLv3): test seams (no C++ counterpart).

namespace ProvingGrounds.Core;

public static partial class PGConfig
{
    // ---- test seams (no C++ counterpart) ---------------------------------------------------------------------------------------

    /// <summary>Test hook: <see cref="Current"/> is <paramref name="state"/> in the calling flow until disposed, which restores what
    /// was there before. Dispose nested overrides in reverse order (use <c>using</c>).</summary>
    internal static IDisposable UseForTest(State state)
    {
        State? previous = s_test.Value;
        s_test.Value = state;
        return new Restore(previous);
    }

    /// <summary>Test hook isolating BOTH globals Load writes: a fresh shipped PGConfig state (not loaded) and a fresh, UNSTAMPED
    /// shipped catalogue, so a test may call <see cref="Load"/> (which stamps the catalogue once). Disposing restores both.</summary>
    internal static IDisposable UseIsolatedForTest()
    {
        IDisposable config = UseForTest(State.Shipped());
        IDisposable catalogue = ArenaRewards.UseForTest(ArenaRewards.Catalogue.Shipped());
        return new Both(catalogue, config);
    }

    /// <summary>Test helper: a clone of <see cref="Current"/> with only the challenge values replaced.</summary>
    internal static IDisposable UseChallengesForTest(Challenges values)
    {
        State state = Current.Clone();
        state.Challenges = values;
        return UseForTest(state);
    }

    /// <summary>Test helper (mirrors golden/stubs/pgconfig_stub.cpp <c>PGConfigStub_Recruitment()</c>): a clone of
    /// <see cref="Current"/> with only <c>g_recruitment</c> replaced.</summary>
    internal static IDisposable UseRecruitmentForTest(Recruitment values)
    {
        State state = Current.Clone();
        state.Recruitment = values;
        return UseForTest(state);
    }

    /// <summary>Test helper (mirrors golden/stubs/pgconfig_stub.cpp <c>PGConfigStub_SetMarksMultiplier</c>): a clone of
    /// <see cref="Current"/> with only <c>g_multiplier</c> replaced.</summary>
    internal static IDisposable UseMarksMultiplierForTest(double multiplier)
    {
        State state = Current.Clone();
        state.Multiplier = multiplier;
        return UseForTest(state);
    }

    /// <summary>Test helper (mirrors golden/stubs/pgconfig_stub.cpp <c>PGConfigStub_Bookie()</c>): a clone of <see cref="Current"/> with only
    /// the bookie wager range replaced.</summary>
    internal static IDisposable UseBookieForTest(Bookie values)
    {
        State state = Current.Clone();
        state.Bookie = values;
        return UseForTest(state);
    }

    /// <summary>Test helper (mirrors <c>PGConfigStub_SetChallengeBase</c>): a clone of <see cref="Current"/> with the three challenge buy-in
    /// bases replaced AT ONCE (division 0 Easy, 1 Medium, 2 Hard; the stub's <c>PGConfigStub_SetChallengeBase</c> sets one division per call).</summary>
    internal static IDisposable UseChallengeBaseForTest(int easy, int medium, int hard)
    {
        State state = Current.Clone();
        state.ChallengeBase = new[] { easy, medium, hard };
        return UseForTest(state);
    }

    /// <summary>Test hooks for the text golden (Tests/Golden/pgconfig_text.json): the formatters behind the default file, the
    /// overlay and the number fields.</summary>
    internal static string DefaultsTextForTest(State state) => DefaultsText(state);
    internal static string OverlayTextForTest(in EditableSettings settings) { FormatEditableOverlay(settings, out string text); return text; }
    internal static string FormatNumberForTest(double value) => FormatNumber(value);
    internal static string QuoteForTest(string value) => Quote(value);
    internal static string SummaryForTest(State state, string origin, string path) => Summary(state, origin, path);
    internal static string RangeWholeForTest(int minimum, int maximum) => RangeWhole(minimum, maximum);
    internal static string RangeNumberForTest(double minimum, double maximum) => RangeNumber(minimum, maximum);

    sealed class Both : IDisposable
    {
        readonly IDisposable _first, _second;
        public Both(IDisposable first, IDisposable second) { _first = first; _second = second; }
        public void Dispose() { _first.Dispose(); _second.Dispose(); }
    }

    sealed class Restore : IDisposable
    {
        readonly State? _previous;
        bool _done;
        public Restore(State? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_test.Value = _previous;
        }
    }
}
