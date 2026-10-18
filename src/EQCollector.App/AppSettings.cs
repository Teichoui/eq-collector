using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQCollector.App;

/// <summary>What the window remembers between runs (%AppData%\EQCollector\settings.json).</summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EQCollector");
    public string EqFolder { get; set; } = "";
    public string CaptureFile { get; set; } = "";
    public string Adapter { get; set; } = "";
    public bool SaveRawCapture { get; set; } = true;
    public string Filter { get; set; } = "udp and (src net 69.174 or dst net 69.174)";

    /// <summary>Send new items and AA ranks to the website while capturing.</summary>
    public bool UploadEnabled { get; set; }

    public string UploadServer { get; set; } = "https://norrathcodex.com";

    /// <summary>The upload token, encrypted for this Windows user (DPAPI) so settings.json never holds it in the clear.</summary>
    public string UploadTokenProtected { get; set; } = "";

    [JsonIgnore]
    public string UploadToken
    {
        get
        {
            if (string.IsNullOrEmpty(UploadTokenProtected)) return "";       // null when the file holds "UploadTokenProtected": null
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(UploadTokenProtected), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return "";      // saved by another Windows user or damaged: the token has to be pasted again
            }
        }
        set => UploadTokenProtected = string.IsNullOrWhiteSpace(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQCollector", "settings.json");

    public static AppSettings Load()
    {
        // settings.json, or the copy kept from before the last save if that one is damaged (a crash mid-write)
        foreach (var file in new[] { FilePath, FilePath + ".bak" })
        {
            var loaded = TryLoad(file);
            if (loaded != null) return loaded;
        }
        return new AppSettings { EqFolder = FindEqFolder() };
    }

    private static AppSettings? TryLoad(string file)
    {
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length < 1024 * 1024)
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings();
                loaded.UploadServer ??= new AppSettings().UploadServer;      // a hand-edited file may hold nulls
                loaded.UploadTokenProtected ??= "";
                loaded.OutputFolder ??= new AppSettings().OutputFolder;
                loaded.EqFolder ??= "";
                loaded.CaptureFile ??= "";
                loaded.Adapter ??= "";
                loaded.Filter ??= new AppSettings().Filter;
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // written to a temporary file and swapped in, so a crash or a full disk can never leave a half-written settings.json
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
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
