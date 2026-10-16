using System.Reflection;

namespace EQCollector.Core;

/// <summary>One decoded item: every field the server sent, in <see cref="ItemDecoder.Columns"/> order.</summary>
public sealed class ItemRecord
{
    public ItemRecord(object[] values) => Values = values;

    public object[] Values { get; }

    public object this[string column] => Values[ItemDecoder.ColumnIndex[column]];

    public long Id => Convert.ToInt64(this["id"]);
    public string Name => (string)this["name"];
    public long Int(string column) => Convert.ToInt64(this[column]);
}

/// <summary>
/// Decodes the full EverQuest item record (all 282 fields) out of the item data the game server sends.
///
/// CREDIT: the layout of the item record -- the order and size of every field, the checks that tell a real record from
/// a false start, and the column list (item_columns.txt) -- comes from kauffman12's eqaainfo project
/// (https://github.com/kauffman12/eqaainfo, itemparse.py and columns.txt), used with the author's permission and
/// credited here as they asked. The code is our own rewrite.
/// </summary>
public static class ItemDecoder
{
    public static readonly string[] Columns = LoadColumns();
    public static readonly Dictionary<string, int> ColumnIndex = BuildIndex();

    private static string[] LoadColumns()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("item_columns.txt")
            ?? throw new InvalidOperationException("item_columns.txt is missing from the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Dictionary<string, int> BuildIndex()
    {
        var index = new Dictionary<string, int>();
        for (int i = 0; i < Columns.Length; i++) index.TryAdd(Columns[i], i);   // a few UNKNOWN names repeat
        return index;
    }

    /// <summary>Reads one record starting at the quantity byte (98 bytes before the name). Throws <see cref="ParseException"/>.</summary>
    public static ItemRecord Decode(ByteCursor cur)
    {
        var d = new List<object>(Columns.Length);

        string convertToName = "";
        long evolveId = 0, evolveLevel = 0, evolveMax = 0, cashLoot = 0;

        cur.U8();                            // quantity
        cur.Skip(50);                        // unknown

        uint convertable = cur.U32();        // also the length of the name that follows
        if (convertable > 0) convertToName = cur.ReadString((int)Math.Min(convertable, int.MaxValue));
        long convertToId = cur.I64();

        byte evolvable = cur.U8();
        if (evolvable != 0)
        {
            evolveId = cur.I32();
            evolveLevel = cur.U8();
            cur.Skip(3);
            cur.Skip(8);                     // how far into the level
            evolveMax = cur.U8();
            cur.Skip(7);
            if (evolveLevel > evolveMax) throw new ParseException("evolve level above max");
        }

        cur.Skip(33);                        // unknown

        byte itemClass = cur.U8();
        if (itemClass > 2) throw new ParseException("item class");
        d.Add((long)itemClass);

        // the name is the first thing that shows a wrong starting point
        string name = cur.ReadString();
        if (name.Length < 2 || name.Length > 254 || !IsPrintable(name)) throw new ParseException("name");
        d.Add(name);

        d.Add(cur.ReadString());             // lore
        d.Add((long)cur.I32());              // idfile
        d.Add((long)cur.I32());              // idfile extra

        int itemId = cur.I32();
        if (itemId == 0) throw new ParseException("item id");
        d.Add((long)itemId);

        d.Add((long)cur.I32());              // weight (tenths)

        for (int i = 0; i < 4; i++) d.Add((long)cur.I8());       // norent, nodrop, attunable, size

        uint slots = cur.U32();
        d.Add((long)slots);

        for (int i = 0; i < 2; i++) d.Add((long)cur.U32());      // price, icon
        for (int i = 0; i < 2; i++) d.Add((long)cur.I8());       // benefit flag, used in tradeskills
        for (int i = 0; i < 6; i++) d.Add((long)cur.I8());       // cold, disease, poison, magic, fire, corruption
        for (int i = 0; i < 7; i++) d.Add((long)cur.I8());       // str, sta, agi, dex, cha, int, wis
        for (int i = 0; i < 7; i++) d.Add((long)cur.I32());      // hp, mana, end, ac, regens

        uint classes = cur.U32();
        if ((classes & ~0x1FFFFu) != 0) throw new ParseException("class mask");   // 17 bits: the 16 player classes and mercenaries
        d.Add((long)classes);

        uint races = cur.U32();
        if ((races & ~0x1FFFFu) != 0) throw new ParseException("race mask");
        d.Add((long)races);

        uint deity = cur.U32();
        if ((deity & ~0x1FFFFu) != 0) throw new ParseException("deity mask");
        d.Add((long)deity);

        for (int i = 0; i < 4; i++) d.Add((long)cur.I32());      // skill mod percent, max, skill, other
        for (int i = 0; i < 4; i++) d.Add((long)cur.I32());      // bane race, body, race amount, body amount

        d.Add((long)cur.I8());                                   // magic

        for (int i = 0; i < 3; i++) d.Add((long)cur.U32());      // food/drink duration, required level, recommended level
        if ((long)d[^1] > 254 || (long)d[^2] > 254) throw new ParseException("level");

        d.Add((long)cur.U32());              // bard type
        d.Add((long)cur.I32());              // bard value
        d.Add((long)cur.I8());               // light
        d.Add((long)cur.U8());               // delay
        d.Add((long)cur.I8());               // element type
        d.Add((long)cur.I8());               // element damage
        d.Add((long)cur.U8());               // range
        d.Add((long)cur.I32());              // damage
        d.Add((long)cur.U32());              // color

        d.Add((long)cur.I32());              // prestige

        byte itemType = cur.U8();
        d.Add((long)itemType);

        for (int i = 0; i < 5; i++) d.Add((long)cur.I32());      // material, material unknown, elite material, hero's forge 1 and 2

        d.Add(cur.F32());                    // sell rate

        for (int i = 0; i < 2; i++) d.Add((long)cur.I32());      // extra damage skill, extra damage amount

        d.Add((long)cur.U32());              // charm file id
        d.Add(cur.ReadString());             // charm file

        for (int i = 0; i < 3; i++) d.Add((long)cur.U32());      // aug type, aug restrictions

        for (int i = 0; i < 6; i++)          // six augment slots
        {
            uint slotType = cur.U32();
            if (slotType > 32) throw new ParseException("augment slot type");
            d.Add((long)slotType);
            d.Add((long)cur.U8());           // visible
            d.Add((long)cur.U8());           // unknown
        }

        for (int i = 0; i < 5; i++) d.Add((long)cur.U32());      // ldon point type, theme, buy price, sell rate, sold
        for (int i = 0; i < 4; i++) d.Add((long)cur.U8());       // bag type, slots, size, weight reduction
        for (int i = 0; i < 2; i++) d.Add((long)cur.I8());       // book type, language

        d.Add(cur.ReadString());             // book file name
        d.Add((long)cur.I32());              // lore group

        d.Add((long)cur.I8());               // artifact
        d.Add((long)cur.U32());              // favor
        d.Add((long)cur.I8());               // fv nodrop

        for (int i = 0; i < 2; i++) d.Add((long)cur.I32());      // attack, haste
        for (int i = 0; i < 2; i++) d.Add((long)cur.U32());      // guild favor, augment distiller

        d.Add((long)cur.U32());              // unknown 1
        d.Add((long)cur.U32());              // unknown 2
        d.Add((long)cur.I8());               // nopet
        d.Add((long)cur.U8());               // unknown 3
        d.Add((long)cur.U32());              // stack size
        d.Add((long)cur.I8());               // notransfer
        d.Add((long)cur.I8());               // expendable arrow

        for (int i = 0; i < 19; i++) d.Add((long)cur.U32());     // unknown, always zero
        d.Add((long)cur.U16());              // unknown 23

        for (int i = 0; i < 7; i++)          // click, proc, worn, focus, scroll, bard, extra
        {
            d.Add((long)cur.I32());          // spell id
            d.Add((long)cur.U8());           // required level
            d.Add((long)cur.I8());           // type
            d.Add((long)cur.I32());          // spell level
            d.Add((long)cur.I32());          // charges
            d.Add((long)cur.U32());          // cast time
            d.Add((long)cur.U32());          // recast delay
            d.Add((long)cur.I32());          // recast type
            d.Add((long)cur.I32());          // proc modifier
            d.Add(cur.ReadString());         // effect name
            d.Add((long)cur.I32());          // unused
        }
        d.Add((long)cur.I32());              // right-click script id

        d.Add((long)cur.I8());               // quest item
        d.Add((long)cur.U32());              // power source cap
        d.Add((long)cur.U32());              // purity
        d.Add((long)cur.U32());              // backstab damage

        for (int i = 0; i < 7; i++) d.Add((long)cur.I32());      // heroic str, int, wis, agi, dex, sta, cha
        for (int i = 0; i < 3; i++) d.Add((long)cur.I32());      // heal amount, spell damage, clairvoyance

        d.Add((long)cur.I8());               // unknown 24

        d.Add((long)cur.U32());              // unknown 25
        d.Add((long)cur.U32());              // unknown 26
        d.Add((long)cur.I8());               // heirloom
        d.Add((long)cur.U8());               // placeable

        for (int i = 0; i < 7; i++) d.Add((long)cur.U32());      // unknown 27-33

        d.Add(cur.ReadString());             // placeable NPC name

        for (int i = 0; i < 7; i++) d.Add((long)cur.U32());      // unknown 34-40

        for (int i = 0; i < 4; i++) d.Add((long)cur.I8());       // collectible, no destroy, no npc, no zone
        for (int i = 0; i < 4; i++) d.Add((long)cur.U8());       // unknown 41-44

        // Some records end here, with a counting pattern in the next bytes (the Singer seals, for one); the original
        // decoder treats three non-trivial bytes at 0, 4 and 8 as that case.
        if (cur.Peek(0) > 1 && cur.Peek(4) > 1 && cur.Peek(8) > 1)
        {
            for (int i = 0; i < 10; i++) d.Add(0L);
        }
        else
        {
            for (int i = 0; i < 4; i++) d.Add((long)cur.I8());   // no ground, unknown 45, marketplace, free storage

            d.Add((long)cur.U8());           // unknown 46
            d.Add((long)cur.U32());          // unknown 47
            d.Add((long)cur.U32());          // unknown 48

            if (itemType == 67)
            {
                d.Add(0L);
                d.Add(0L);                   // no luck
                d.Add((long)cur.I32());      // lore equipped group
            }
            else
            {
                int minLuck = cur.I32();
                int maxLuck = cur.I32();
                if (minLuck > 0)
                {
                    d.Add((long)minLuck);
                    d.Add((long)maxLuck);
                    d.Add((long)cur.I32());  // lore equipped group
                }
                else if (maxLuck != 0)
                {
                    d.Add(0L);
                    d.Add(0L);
                    d.Add((long)maxLuck);    // (read as the lore equipped group)
                }
                else
                {
                    d.Add(0L);
                    d.Add(0L);
                    if (slots == 0)
                    {
                        cashLoot = cur.I32();
                        d.Add(0L);
                    }
                    else
                    {
                        d.Add((long)cur.I32());   // lore equipped group
                    }
                }
            }
        }

        d.Add(cashLoot);

        d.Add((long)evolvable);
        d.Add(evolveId);
        d.Add(evolveLevel);
        d.Add(evolveMax);

        d.Add((long)convertable);
        d.Add(convertToId);
        d.Add(convertToName);

        // charm text, extra text, made-by and update time are filled in from other packets
        d.Add(""); d.Add(""); d.Add(""); d.Add("");

        if (d.Count != Columns.Length) throw new ParseException($"field count {d.Count}, expected {Columns.Length}");
        return new ItemRecord(d.ToArray());
    }

    private static bool IsPrintable(string s)
    {
        foreach (char c in s)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }
}
