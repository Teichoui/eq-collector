namespace EQCollector.Core;

/// <summary>Finds item records inside a decoded application packet.</summary>
public static class ItemFinder
{
    private const int MaxScanned = 8 * 1024 * 1024;   // packets larger than this are not scanned
    private const int MinPacket = 800;     // a packet shorter than this is not scanned for tokened records
    private const int TokenLength = 16;    // the printable token that starts each tokened record
    private const int RecordHeader = 98;   // bytes from the start of a record to its name (quantity .. item class)

    public readonly record struct Found(int Offset, ItemRecord Item);

    /// <summary>
    /// Records that start with the 16-character token (inventory dumps, vendor lists) are found first; the rest of the
    /// packet is then searched for records with no token (an examine reply, a loot or merchant entry), trying to decode
    /// at every offset where an item class byte and a printable name could be. Every field is range-checked as it is
    /// read, so a wrong offset is rejected quickly.
    /// </summary>
    public static List<Found> Find(byte[] data, bool withoutToken = true)
    {
        if (data.Length > MaxScanned) return new List<Found>();     // no real item packet is this big; bound the work on hostile input
        var found = FindTokened(data);
        if (!withoutToken) return found;

        var seen = new HashSet<long>(found.Select(f => f.Item.Id));
        foreach (var f in FindTokenless(data))
        {
            if (seen.Add(f.Item.Id)) found.Add(f);      // an id already found by its token is the same record
        }
        found.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return found;
    }

    private static bool Acceptable(byte b) => b > 42 && b < 123 && b != 47 && b != 64 && b != 92;

    private static List<Found> FindTokened(byte[] data)
    {
        var found = new List<Found>();
        int pos = 0;
        int n = data.Length;

        while (n - pos > MinPacket)
        {
            int run = 0;
            int i = pos;
            while (i < n && run < TokenLength)
            {
                run = Acceptable(data[i]) ? run + 1 : 0;
                i++;
            }
            int start = i - run;
            if (run < TokenLength) break;               // no token in the rest of the packet

            // the token must be exactly 16 characters: a longer run of characters is not one
            int end = start;
            while (end < n && data[end] != 0 && end - start < TokenLength + 4) end++;
            int tokenLength = end - start;
            int after = end < n && data[end] == 0 ? end + 1 : end;
            pos = after;
            if (tokenLength != TokenLength) continue;

            var cur = new ByteCursor(data, after);
            try
            {
                var item = ItemDecoder.Decode(cur);
                found.Add(new Found(start, item));
                pos = cur.Position;
            }
            catch (ParseException)
            {
            }
        }
        return found;
    }

    private static List<Found> FindTokenless(byte[] data)
    {
        var found = new List<Found>();
        int n = data.Length;
        int pos = 0;
        while (pos + RecordHeader + 4 < n)
        {
            byte klass = data[pos + RecordHeader - 1];
            byte first = data[pos + RecordHeader];
            byte second = data[pos + RecordHeader + 1];
            if (klass <= 2 && first >= 32 && first < 127 && second >= 32 && second < 127)
            {
                var cur = new ByteCursor(data, pos);
                try
                {
                    var item = ItemDecoder.Decode(cur);
                    found.Add(new Found(pos, item));
                    pos = cur.Position;
                    continue;
                }
                catch (ParseException)
                {
                }
            }
            pos++;
        }
        return found;
    }
}
