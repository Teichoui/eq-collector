using System.Net.NetworkInformation;
using System.Net.Sockets;
using EQCollector.Core;
using SharpPcap;
using SharpPcap.LibPcap;

namespace EQCollector.App;

/// <summary>A network adapter Npcap can listen on.</summary>
public sealed class AdapterInfo
{
    public required ILiveDevice Device { get; init; }
    public required string Display { get; init; }
    public string? Ipv4 { get; init; }
    public override string ToString() => Display;
}

public static class Adapters
{
    public static bool NpcapInstalled =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll")) || File.Exists(Path.Combine(Environment.SystemDirectory, "wpcap.dll"));

    public static List<AdapterInfo> List()
    {
        var result = new List<AdapterInfo>();
        foreach (var device in CaptureDeviceList.Instance)
        {
            string? ipv4 = null;
            string name = device.Description ?? device.Name;
            if (device is LibPcapLiveDevice lp)
            {
                ipv4 = lp.Interface?.Addresses?
                    .Select(a => a.Addr?.ipAddress)
                    .FirstOrDefault(ip => ip != null && ip.AddressFamily == AddressFamily.InterNetwork)?.ToString();
                if (!string.IsNullOrWhiteSpace(lp.Interface?.FriendlyName)) name = lp.Interface!.FriendlyName;
            }
            result.Add(new AdapterInfo { Device = device, Display = ipv4 != null ? $"{name}  ({ipv4})" : name, Ipv4 = ipv4 });
        }
        return result;
    }

    /// <summary>The adapter that has the default gateway -- the one the game's traffic goes out on.</summary>
    public static int DefaultIndex(List<AdapterInfo> adapters)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var props = nic.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any))) continue;
            var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            int i = adapters.FindIndex(a => a.Ipv4 == ip);
            if (i >= 0) return i;
        }
        return adapters.Count > 0 ? 0 : -1;
    }
}

/// <summary>
/// Listens on one adapter and hands every UDP packet that passes the filter to a callback. Nothing the callback does can
/// escape into SharpPcap's capture thread: a failure is reported through <see cref="Failed"/> and the capture goes on
/// (or, for the callback to decide, is stopped from the UI).
/// </summary>
public sealed class LiveCapture : IDisposable
{
    private readonly ILiveDevice _device;
    private readonly Action<UdpPacket> _onUdp;
    private bool _running;
    private bool _disposed;

    /// <summary>Raised (on the capture thread) when handling a packet threw.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Every frame that passed the filter, as captured (link type, time in seconds, bytes) -- for saving the raw traffic.</summary>
    public event Action<int, double, byte[]>? Frame;

    public LiveCapture(ILiveDevice device, string filter, Action<UdpPacket> onUdp)
    {
        _device = device;
        _onUdp = onUdp;
        _device.Open(DeviceModes.None, 1000);
        try
        {
            if (!string.IsNullOrWhiteSpace(filter)) _device.Filter = filter;
            _device.OnPacketArrival += OnArrival;
        }
        catch
        {
            _device.Close();                 // do not leave the adapter open when setting it up failed
            throw;
        }
    }

    public void Start()
    {
        _device.StartCapture();
        _running = true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _device.StopCapture();
    }

    private void OnArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            double time = raw.Timeval.Seconds + raw.Timeval.MicroSeconds / 1e6;
            Frame?.Invoke((int)raw.LinkLayerType, time, raw.Data);
            if (FrameParser.TryParseUdp((int)raw.LinkLayerType, raw.Data, time, out var udp)) _onUdp(udp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { Failed?.Invoke(ex); } catch (Exception) { /* a failing error handler must not reach the capture thread either */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Stop();
        }
        finally
        {
            _device.OnPacketArrival -= OnArrival;
            _device.Close();
        }
    }
}
