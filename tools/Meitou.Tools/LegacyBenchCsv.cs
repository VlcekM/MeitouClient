using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// The old Python kit's <c>results.csv</c> (its columns: <c>bench/kit/results-format.md</c> before the kit was removed) read as JSON row objects
/// for <see cref="BenchImport"/>.
/// </summary>
static class LegacyBenchCsv
{
    /// <summary>
    /// The columns that hold text; every other column is a number (or blank, left out).
    /// </summary>
    static readonly HashSet<string> TextColumns =
    [
        "date", "time", "build", "commit", "platform", "data", "save", "segments", "gpu", "nvidia_driver", "note", "machine", "cpu", "os", "kernel",
        "resolution",
    ];

    /// <summary>
    /// A results CSV's rows as objects by its header: text columns as strings, the others as numbers, blanks left out.
    /// </summary>
    public static IEnumerable<JsonObject> Rows(string csv)
    {
        var records = Parse(csv.TrimStart('﻿'));
        if (records.Count == 0) yield break;

        var header = records[0];
        foreach (var record in records.Skip(1).Where(r => r.Count > 1))
        {
            var row = new JsonObject();
            for (int i = 0; i < header.Count && i < record.Count; i++)
            {
                if (Cell(header[i], record[i]) is { } value) row[header[i]] = value;
            }

            yield return row;
        }
    }

    /// <summary>
    /// One cell as JSON: null when blank, a string in a text column, else a number (an integer when written without a point).
    /// </summary>
    static JsonNode? Cell(string column, string text)
    {
        if (text.Length == 0) return null;
        if (TextColumns.Contains(column)) return JsonValue.Create(text);
        if (!text.Contains('.') && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            return JsonValue.Create(integer);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            return JsonValue.Create(number);

        return JsonValue.Create(text);
    }

    /// <summary>
    /// The records of a CSV text: fields split at commas; a quoted field may hold commas, line breaks and doubled quotes.
    /// </summary>
    static List<List<string>> Parse(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') field.Append(text[++i]);
                else quoted = false;
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                EndField(record, field);
            }
            else if (c == '\n')
            {
                EndField(record, field);
                records.Add(record);
                record = [];
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            EndField(record, field);
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Adds the field being read to the record and starts the next field.
    /// </summary>
    static void EndField(List<string> record, StringBuilder field)
    {
        record.Add(field.ToString());
        field.Clear();
    }
}
