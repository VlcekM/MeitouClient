using System.Globalization;

namespace Meitou.Localisation;

/// <summary>
/// The plural selection of a gettext catalog: its header's <c>Plural-Forms: nplurals=N; plural=EXPR;</c>
/// (docs/formats/gettext.md). <c>EXPR</c> is a C expression of <c>n</c> (integers, <c>n</c>, parentheses, <c>?:</c>, <c>||</c>,
/// <c>&amp;&amp;</c>, <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>+</c>, <c>-</c>, <c>*</c>,
/// <c>/</c>, <c>%</c>, <c>!</c>), evaluated here by a small parser instead of by name or language.
/// </summary>
public sealed class PluralRule
{
    readonly Func<long, long> _select;

    PluralRule(int forms, Func<long, long> select) => (Forms, this._select) = (forms, select);

    /// <summary>
    /// How many plural forms a message has.
    /// </summary>
    public int Forms { get; }

    /// <summary>
    /// The form index for <paramref name="n"/> (clamped to the valid range).
    /// </summary>
    public int Index(long n) => (int)Math.Clamp(_select(Math.Abs(n)), 0, Forms - 1);

    /// <summary>
    /// English and the other Germanic languages: two forms, the second for everything but one.
    /// </summary>
    public static PluralRule Germanic { get; } = new(2, n => n != 1 ? 1 : 0);

    /// <summary>
    /// The rule of a <c>Plural-Forms</c> header value; <see cref="Germanic"/> when it is missing or unreadable.
    /// </summary>
    public static PluralRule Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return Germanic;
        int forms = 0;
        string? expression = null;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string key = part[..eq].Trim(), value = part[(eq + 1)..].Trim();
            if (key == "nplurals") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out forms);
            else if (key == "plural") expression = value;
        }
        if (forms < 1 || expression is null) return Germanic;
        try
        {
            return new(forms, new Parser(expression).Parse());
        }
        catch (FormatException) { return Germanic; }
    }

    // Recursive descent over the C operator precedence; every node is a closure over n, so evaluation allocates nothing.
    sealed class Parser(string text)
    {
        int _at;

        public Func<long, long> Parse()
        {
            var result = Ternary();
            Skip();
            if (_at != text.Length) throw new FormatException("unexpected text in plural expression");
            return result;
        }

        void Skip() { while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++; }

        bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, _at, token, 0, token.Length) != 0) return false;

            // "<" must not eat the first character of "<=", "!" not of "!=", "=" is only valid as "==".
            if (token.Length == 1 && _at + 1 < text.Length && text[_at + 1] == '=' && token[0] is '<' or '>' or '!')
                return false;
            _at += token.Length;
            return true;
        }

        Func<long, long> Ternary()
        {
            var condition = Or();
            if (!Take("?")) return condition;
            var yes = Ternary();
            if (!Take(":")) throw new FormatException("missing ':'");
            var no = Ternary();
            return n => condition(n) != 0 ? yes(n) : no(n);
        }

        Func<long, long> Or()
        {
            var left = And();
            while (Take("||"))
            {
                var l = left;
                var r = And();
                left = n => l(n) != 0 || r(n) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<long, long> And()
        {
            var left = Equality();
            while (Take("&&"))
            {
                var l = left;
                var r = Equality();
                left = n => l(n) != 0 && r(n) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<long, long> Equality()
        {
            var left = Relational();
            while (true)
            {
                var l = left;
                if (Take("=="))
                {
                    var r = Relational();
                    left = n => l(n) == r(n) ? 1 : 0;
                }
                else if (Take("!="))
                {
                    var r = Relational();
                    left = n => l(n) != r(n) ? 1 : 0;
                }
                else return left;
            }
        }

        Func<long, long> Relational()
        {
            var left = Additive();
            while (true)
            {
                var l = left;
                if (Take("<="))
                {
                    var r = Additive();
                    left = n => l(n) <= r(n) ? 1 : 0;
                }
                else if (Take(">="))
                {
                    var r = Additive();
                    left = n => l(n) >= r(n) ? 1 : 0;
                }
                else if (Take("<"))
                {
                    var r = Additive();
                    left = n => l(n) < r(n) ? 1 : 0;
                }
                else if (Take(">"))
                {
                    var r = Additive();
                    left = n => l(n) > r(n) ? 1 : 0;
                }
                else return left;
            }
        }

        Func<long, long> Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                var l = left;
                if (Take("+"))
                {
                    var r = Multiplicative();
                    left = n => l(n) + r(n);
                }
                else if (Take("-"))
                {
                    var r = Multiplicative();
                    left = n => l(n) - r(n);
                }
                else return left;
            }
        }

        Func<long, long> Multiplicative()
        {
            var left = Unary();
            while (true)
            {
                var l = left;
                if (Take("*"))
                {
                    var r = Unary();
                    left = n => l(n) * r(n);
                }
                else if (Take("/"))
                {
                    var r = Unary();
                    left = n => r(n) is var d and not 0 ? l(n) / d : 0;
                }
                else if (Take("%"))
                {
                    var r = Unary();
                    left = n => r(n) is var d and not 0 ? l(n) % d : 0;
                }
                else return left;
            }
        }

        Func<long, long> Unary()
        {
            if (Take("!"))
            {
                var operand = Unary();
                return n => operand(n) == 0 ? 1 : 0;
            }
            return Primary();
        }

        Func<long, long> Primary()
        {
            Skip();
            if (_at >= text.Length) throw new FormatException("unexpected end of plural expression");
            if (text[_at] == '(')
            {
                _at++;
                var inner = Ternary();
                if (!Take(")")) throw new FormatException("missing ')'");
                return inner;
            }
            if (text[_at] == 'n')
            {
                _at++;
                return n => n;
            }
            int start = _at;
            while (_at < text.Length && char.IsAsciiDigit(text[_at])) _at++;
            if (start == _at || !long.TryParse(text.AsSpan(start, _at - start), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                throw new FormatException("bad token in plural expression");
            return _ => value;
        }
    }
}