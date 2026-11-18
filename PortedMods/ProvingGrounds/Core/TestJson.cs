namespace ProvingGrounds.Core;

/// <summary>Port only (task 8.8): numbers for the TEST-ONLY verbs' JSON answers (`pg town ...`, `pg items`, `pg ring`, ...). JSON has no
/// NaN or infinity, and msvcr's text for them (<c>-1.#IND</c>, <c>1.#INF</c>) makes the whole line unparseable, so a non-finite value is
/// written <c>null</c>; a finite one exactly as <see cref="CMath.FormatG(double, int)"/> / <see cref="CMath.FormatF"/> write it. Never used for
/// player-visible text or the C++ log lines, which keep msvcr's formatting.</summary>
public static class TestJson
{
    /// <summary><see cref="CMath.FormatG(double, int)"/> (precision 9 by default), or <c>null</c> for NaN / infinity.</summary>
    public static string G(double value, int precision = 9) => double.IsFinite(value) ? CMath.FormatG(value, precision) : "null";

    /// <summary><see cref="CMath.FormatF"/>, or <c>null</c> for NaN / infinity.</summary>
    public static string F(double value, int decimals) => double.IsFinite(value) ? CMath.FormatF(value, decimals) : "null";
}
