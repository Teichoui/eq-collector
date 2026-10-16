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
    ///   EQCollector.exe --file capture.pcapng [--out folder] [--eq "EverQuest folder"]
    /// reads a capture file, writes items.jsonl and aa.jsonl, prints a summary and exits.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
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

        try
        {
            var engine = CollectorEngine.ProcessFile(file, outDir, strings, Console.WriteLine, n => Console.Write($"\r{n:N0} packets"));
            Console.WriteLine();
            Console.WriteLine($"{engine.Packets:N0} packets, {engine.ItemCount:N0} items, {engine.AaCount:N0} AA ranks -> {outDir}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("EQCollector: " + ex.Message);      // not a capture file, or the output folder cannot be written
            return 1;
        }
    }
}
