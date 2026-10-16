using System.Text.RegularExpressions;

namespace EQCollector.Core;

/// <summary>
/// Titles and descriptions the AA packets only carry as string ids, and spell names, read from the client's own files
/// (dbstr_us.txt and spells_us.txt in the EverQuest folder).
/// </summary>
public sealed class ClientStrings
{
    private static readonly Regex DbStr = new(@"^(\d+)\^(\d)\^([^^]+)\^(0|33)\^$", RegexOptions.Compiled);
    private static readonly Regex SpellLine = new(@"^(\d+)\^([^^]+)\^", RegexOptions.Compiled);

    public Dictionary<int, string> Titles { get; } = new();
    public Dictionary<int, string> Descriptions { get; } = new();
    public Dictionary<int, string> Spells { get; } = new();

    public static ClientStrings Empty => new();

    /// <summary>Loads from an EverQuest folder (or any folder holding the two files). Missing files just leave that part empty.</summary>
    public static ClientStrings LoadFolder(string folder)
    {
        var s = new ClientStrings();
        string dbstr = Path.Combine(folder, "dbstr_us.txt");
        string spells = Path.Combine(folder, "spells_us.txt");
        if (File.Exists(dbstr)) s.LoadDbStr(dbstr);
        if (File.Exists(spells)) s.LoadSpells(spells);
        return s;
    }

    public void LoadDbStr(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var m = DbStr.Match(line.TrimEnd('\r', '\n'));
            if (!m.Success) continue;
            int id = int.Parse(m.Groups[1].Value);
            if (m.Groups[2].Value == "1") Titles[id] = m.Groups[3].Value;
            else if (m.Groups[2].Value == "4") Descriptions[id] = m.Groups[3].Value;
        }
    }

    public void LoadSpells(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var m = SpellLine.Match(line);
            if (m.Success) Spells[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        }
    }
}
