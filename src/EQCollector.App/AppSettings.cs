using System.Text.Json;

namespace EQCollector.App;

/// <summary>What the window remembers between runs (%AppData%\EQCollector\settings.json).</summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EQCollector");
    public string EqFolder { get; set; } = "";
    public string CaptureFile { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Filter { get; set; } = "udp and (src net 69.174 or dst net 69.174)";

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQCollector", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        var s = new AppSettings { EqFolder = FindEqFolder() };
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The EverQuest folder, when it is in one of the usual places (the one holding dbstr_us.txt).</summary>
    public static string FindEqFolder()
    {
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            candidates.Add(Path.Combine(drive.Name, "EverQuest"));
            candidates.Add(Path.Combine(drive.Name, "Program Files (x86)", "Sony", "EverQuest"));
            candidates.Add(Path.Combine(drive.Name, "Program Files (x86)", "Daybreak Game Company", "Installed Games", "EverQuest"));
            candidates.Add(Path.Combine(drive.Name, "Users", "Public", "Daybreak Game Company", "Installed Games", "EverQuest"));
        }
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "dbstr_us.txt"))) ?? "";
    }
}
