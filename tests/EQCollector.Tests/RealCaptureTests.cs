using EQCollector.Core;
using Xunit;
using Xunit.Abstractions;

namespace EQCollector.Tests;

/// <summary>
/// Runs the real capture (items confirmed against in-game tooltips). The tests quietly pass when the capture is not on this
/// machine. Set EQC_CAPTURE to use another file; set EQC_FULL=1 to also run the whole-file AA test (slow).
/// </summary>
public class RealCaptureTests
{
    private static readonly string Capture = Environment.GetEnvironmentVariable("EQC_CAPTURE") ?? "";
    private static readonly string Strings = Environment.GetEnvironmentVariable("EQC_STRINGS") ?? @"D:\EQ test";
    private readonly ITestOutputHelper _out;

    public RealCaptureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void The_four_confirmed_items_decode_from_the_first_40000_packets()
    {
        if (!File.Exists(Capture)) { _out.WriteLine("capture not found, skipped"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        var engine = new CollectorEngine(dir);
        foreach (var udp in PcapReader.ReadUdp(Capture).Take(40000)) engine.Feed(udp);
        engine.Close();

        _out.WriteLine($"{engine.ItemCount} distinct items from {engine.Packets} packets");
        Assert.InRange(engine.ItemCount, 1800, 2100);

        var byId = engine.Items.ToDictionary(i => i.Id);
        Assert.Equal("Fractured Arcane Chain Helm Ornament", byId[172403].Name);
        Assert.Equal("Fractured Arcane Chain Legs Ornament", byId[172408].Name);
        Assert.Equal("Fractured Arcane Cloth Helm Ornament", byId[172417].Name);

        var icon = byId[175376];
        Assert.Equal("Blazing Icon of Solusek Ro", icon.Name);
        Assert.Equal(69, icon.Int("heroic_str"));
        Assert.Equal(45, icon.Int("heroic_sta"));
        Assert.Equal(69, icon.Int("heroic_agi"));
        Assert.Equal(148, icon.Int("heroic_dex"));
        Assert.Equal(59, icon.Int("heroic_wis"));
        Assert.Equal(59, icon.Int("heroic_int"));
        Assert.Equal(69, icon.Int("heroic_cha"));
        Assert.Equal(130, icon.Int("reqlevel"));

        Assert.True(File.Exists(Path.Combine(dir, "items.jsonl")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void The_whole_capture_gives_the_AA_table()
    {
        if (Environment.GetEnvironmentVariable("EQC_FULL") != "1") { _out.WriteLine("set EQC_FULL=1 to run, skipped"); return; }
        if (!File.Exists(Capture)) { _out.WriteLine("capture not found, skipped"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        var strings = Directory.Exists(Strings) ? ClientStrings.LoadFolder(Strings) : null;
        var engine = CollectorEngine.ProcessFile(Capture, dir, strings, s => _out.WriteLine(s));

        _out.WriteLine($"{engine.ItemCount} items, {engine.AaCount} AA ranks");
        Assert.Equal((ushort)0x30cf, engine.AaOpcode);
        Assert.InRange(engine.AaCount, 700, 900);
        Directory.Delete(dir, true);
    }
}
