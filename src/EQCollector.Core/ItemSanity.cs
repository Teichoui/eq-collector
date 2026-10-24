using System.Text.Json;

namespace EQCollector.Core;

/// <summary>
/// Tells a real item record from a mis-decoded one. The packet scan occasionally lines up on the wrong bytes and produces
/// a record whose fields are noise (an item id of 1.9 billion, a price over 4 billion, "yes/no" fields holding -46, control
/// characters in the text). Such a record is never written, shown or uploaded. The limits are loose on purpose: they are
/// values no real item can have, checked against thousands of real records, not a judgement about what looks plausible.
/// </summary>
public static class ItemSanity
{
    /// <summary>Real item ids stop well below this (the highest seen is under 200,000).</summary>
    public const long MaxItemId = 5_000_000;

    /// <summary>Longest item name in the game is about 50 characters.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Fields that are always 0 or 1 in real records.</summary>
    private static readonly string[] YesNoFlags = { "nodestroy", "noground", "nonpc" };

    /// <summary>Null when the record looks like a real item, otherwise a short reason it does not.</summary>
    public static string? Problem(ItemRecord item)
    {
        if (item.Values.Length != ItemDecoder.Columns.Length) return "wrong number of fields";
        return Check(c => item[c], ItemDecoder.Columns);
    }

    /// <summary>
    /// The same check for an items.jsonl / upload record (values may be JsonElements). An abbreviated record is allowed:
    /// price and the yes/no flags are checked when they are present; the id and the name are always required.
    /// </summary>
    public static string? Problem(IReadOnlyDictionary<string, object?> record) =>
        Check(c => record.TryGetValue(c, out var v) ? v : null, record.Keys);

    private static string? Check(Func<string, object?> get, IEnumerable<string> columns)
    {
        if (!TryLong(get("id"), out long id) || id < 1 || id > MaxItemId) return "item id out of range";

        object? price = get("price");
        if (price != null && (!TryLong(price, out long p) || p < 0 || p > int.MaxValue)) return "price out of range";

        string? name = AsString(get("name"));
        if (name == null || name.Trim().Length == 0 || name.Length > MaxNameLength || name.Trim().All(char.IsDigit))
            return "item name is not a name";

        foreach (string flag in YesNoFlags)
        {
            object? v = get(flag);
            if (v != null && (!TryLong(v, out long f) || f is < 0 or > 1)) return $"{flag} is not 0 or 1";
        }

        foreach (string column in columns)
        {
            string? text = AsString(get(column));
            if (text != null && HasControlCharacter(text)) return $"control character in {column}";
        }

        return null;
    }

    /// <summary>Any control character (C0, DEL or C1) except tab, carriage return and line feed.</summary>
    private static bool HasControlCharacter(string s)
    {
        foreach (char c in s)
            if (char.IsControl(c) && c != '\t' && c != '\n' && c != '\r') return true;
        return false;
    }

    private static string? AsString(object? v) => v switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    /// <summary>Whole numbers only: a float, decimal, bool, char or text is not a valid value for these fields.</summary>
    private static bool TryLong(object? v, out long value)
    {
        value = 0;
        switch (v)
        {
            case sbyte x: value = x; return true;
            case byte x: value = x; return true;
            case short x: value = x; return true;
            case ushort x: value = x; return true;
            case int x: value = x; return true;
            case uint x: value = x; return true;
            case long x: value = x; return true;
            case ulong x when x <= long.MaxValue: value = (long)x; return true;
            case JsonElement e: return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
            default: return false;
        }
    }
}
