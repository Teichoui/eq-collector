using System.Runtime.InteropServices;
using EQCollector.Core;

namespace EQCollector.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    /// <summary>
    /// With no arguments the window opens. For scripts and testing:
    ///   EQCollector.exe --file capture.pcapng [--out folder] [--eq "EverQuest folder"] [--upload]
    /// reads a capture file, writes items.jsonl and aa.jsonl, prints a summary and exits (with --upload it then sends them
    /// to the website). EQCollector.exe --upload-folder folder  sends what a folder already holds.
    /// The website and token come from --server and the EQC_TOKEN environment variable, or from what the window saved.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--upload-folder")) return RunUploadFolder(args);
        if (args.Contains("--file")) return RunHeadless(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int RunUploadFolder(string[] args)
    {
        AttachConsole(AttachParentProcess);
        string? folder = Arg(args, "--upload-folder");
        if (folder == null || !Directory.Exists(folder))
        {
            Console.Error.WriteLine("EQCollector --upload-folder <results folder> [--server https://...]   (token: EQC_TOKEN environment variable or saved by the window)");
            return 2;
        }
        return UploadFolder(args, folder);
    }

    private static int UploadFolder(string[] args, string folder)
    {
        var settings = AppSettings.Load();
        string server = Arg(args, "--server") ?? settings.UploadServer;
        var envToken = Environment.GetEnvironmentVariable("EQC_TOKEN");
        string token = string.IsNullOrWhiteSpace(envToken) ? settings.UploadToken : envToken;
        try
        {
            using var uploader = new CollectorUploader(server, token, folder, Console.WriteLine);
            var r = uploader.UploadFolderAsync(folder).GetAwaiter().GetResult();
            Console.WriteLine($"Uploaded: {r.ItemsChecked:N0} items checked, {r.ItemsAdded:N0} added, {r.ItemsQueued:N0} queued for review, {r.AaSaved:N0} AA ranks saved, {r.Rejected:N0} rejected.");
            return 0;
        }
        catch (Exception ex) when (ex is UploadAuthException or UploadUnavailableException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("EQCollector: " + ex.Message);
            return 1;
        }
    }

    private static int RunHeadless(string[] args)
    {
        AttachConsole(AttachParentProcess);
        string? file = Arg(args, "--file");
        if (file == null || !File.Exists(file))
        {
            Console.Error.WriteLine("EQCollector --file <capture.pcap|pcapng> [--out <folder>] [--eq <EverQuest folder>]");
            return 2;
        }

        string outDir = Arg(args, "--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EQCollector");
        string? eq = Arg(args, "--eq") ?? AppSettings.FindEqFolder();
        var strings = !string.IsNullOrEmpty(eq) && Directory.Exists(eq) ? ClientStrings.LoadFolder(eq) : ClientStrings.Empty;
        foreach (var error in strings.LoadErrors) Console.Error.WriteLine("EQCollector: could not read a client file, carrying on without it -- " + error);

        try
        {
            var engine = CollectorEngine.ProcessFile(file, outDir, strings, Console.WriteLine, n => Console.Write($"\r{n:N0} packets"));
            Console.WriteLine();
            Console.WriteLine($"{engine.Packets:N0} packets, {engine.ItemCount:N0} items, {engine.AaCount:N0} AA ranks -> {outDir}");
            return args.Contains("--upload") ? UploadFolder(args, outDir) : 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("EQCollector: " + ex.Message);      // not a capture file, or the output folder cannot be written
            return 1;
        }
    }
}
