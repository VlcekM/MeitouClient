using System.Globalization;
using System.Text;
using System.Text.Json;
using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// The <c>json.encode</c> / <c>json.decode</c> codec exposed to Lua mods: depth-limited, and tolerant (anything it cannot map becomes nil / null).
/// </summary>
internal static class LuaJson
{
    #region Encode

    /// <summary>
    /// Serializes a Lua value to JSON text; values beyond depth 12 and kinds JSON cannot hold (functions, userdata) become <c>null</c>.
    /// </summary>
    internal static string Encode(DynValue v, int depth)
    {
        if (depth > 12) return "null";

        return v.Type switch
        {
            DataType.Nil or DataType.Void => "null",
            DataType.Boolean => v.Boolean ? "true" : "false",
            DataType.Number => EncodeNumber(v.Number),
            DataType.String => JsonSerializer.Serialize(v.String),
            DataType.Table => EncodeTable(v.Table, depth + 1),
            _ => "null",
        };
    }

    /// <summary>
    /// A finite number as its round-trip text; NaN and infinities become <c>null</c>.
    /// </summary>
    private static string EncodeNumber(double number)
    {
        return double.IsFinite(number) ? number.ToString("G17", CultureInfo.InvariantCulture) : "null";
    }

    /// <summary>
    /// A table as a JSON array when its keys are exactly 1..n, otherwise as an object.
    /// </summary>
    private static string EncodeTable(Table t, int depth)
    {
        int n = t.Length;
        bool isArr = n > 0 && t.Pairs.All(p => IsArrayKey(p.Key, n));

        return isArr ? EncodeArray(t, n, depth) : EncodeObject(t, depth);
    }

    /// <summary>
    /// Whether a table key is an integer index inside 1..<paramref name="n"/>.
    /// </summary>
    private static bool IsArrayKey(DynValue key, int n)
    {
        return key.Type == DataType.Number && key.Number >= 1 && key.Number <= n && Math.Floor(key.Number) == key.Number;
    }

    /// <summary>
    /// The entries 1..<paramref name="n"/> of a table as a JSON array.
    /// </summary>
    private static string EncodeArray(Table t, int n, int depth)
    {
        StringBuilder sb = new();
        sb.Append('[');

        for (int i = 1; i <= n; i++)
        {
            if (i > 1) sb.Append(',');
            sb.Append(Encode(t.Get(i), depth));
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// A table as a JSON object; entries whose key is neither a string nor a number are skipped.
    /// </summary>
    private static string EncodeObject(Table t, int depth)
    {
        StringBuilder sb = new();
        sb.Append('{');
        bool first = true;

        foreach (TablePair p in t.Pairs)
        {
            if (p.Key.Type is not (DataType.String or DataType.Number)) continue;

            if (!first) sb.Append(',');
            first = false;

            sb.Append(JsonSerializer.Serialize(KeyText(p.Key)));
            sb.Append(':');
            sb.Append(Encode(p.Value, depth));
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// The JSON member name of a string or number table key.
    /// </summary>
    private static string KeyText(DynValue key)
    {
        return key.Type == DataType.String ? key.String : key.Number.ToString(CultureInfo.InvariantCulture);
    }

    #endregion

    #region Decode

    /// <summary>
    /// Parses JSON text into a Lua value; blank or over-256-KiB text and malformed JSON give nil.
    /// </summary>
    internal static DynValue Decode(Script s, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 256 * 1024) return DynValue.Nil;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            return ConvertElement(s, doc.RootElement, 0);
        }
        catch (Exception)
        {
            // Malformed JSON is not an error for the mod: it just gets nil.
            return DynValue.Nil;
        }
    }

    /// <summary>
    /// One JSON element as a Lua value; nesting beyond depth 12 becomes nil.
    /// </summary>
    private static DynValue ConvertElement(Script s, JsonElement el, int depth)
    {
        if (depth > 12) return DynValue.Nil;

        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DynValue.Nil,
            JsonValueKind.True => DynValue.True,
            JsonValueKind.False => DynValue.False,
            JsonValueKind.Number => DynValue.NewNumber(el.GetDouble()),
            JsonValueKind.String => DynValue.NewString(el.GetString() ?? ""),
            JsonValueKind.Array => ConvertArray(s, el, depth),
            JsonValueKind.Object => ConvertObject(s, el, depth),
            _ => DynValue.Nil,
        };
    }

    /// <summary>
    /// A JSON array as a 1-based Lua table.
    /// </summary>
    private static DynValue ConvertArray(Script s, JsonElement el, int depth)
    {
        Table t = new(s);
        int i = 1;

        foreach (JsonElement item in el.EnumerateArray())
        {
            t[i++] = ConvertElement(s, item, depth + 1);
        }

        return DynValue.NewTable(t);
    }

    /// <summary>
    /// A JSON object as a Lua table keyed by member name.
    /// </summary>
    private static DynValue ConvertObject(Script s, JsonElement el, int depth)
    {
        Table t = new(s);

        foreach (JsonProperty prop in el.EnumerateObject())
        {
            t[prop.Name] = ConvertElement(s, prop.Value, depth + 1);
        }

        return DynValue.NewTable(t);
    }

    #endregion
}
