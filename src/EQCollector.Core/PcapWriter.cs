namespace EQCollector.Core;

/// <summary>
/// Writes captured frames to a classic pcap file (microsecond timestamps, little-endian) that Wireshark and this
/// program's own file mode can read. Used to keep the raw traffic of a live session for troubleshooting.
/// </summary>
public sealed class PcapWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private int _sinceFlush;

    public string Path { get; }
    public long Frames { get; private set; }

    public PcapWriter(string path, int linkType)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);   // never truncates an earlier capture
        try
        {
            _writer = new BinaryWriter(_stream);
            _writer.Write(0xA1B2C3D4u);        // magic: microsecond timestamps
            _writer.Write((ushort)2);          // version 2.4
            _writer.Write((ushort)4);
            _writer.Write(0);                  // time zone
            _writer.Write(0u);                 // timestamp accuracy
            _writer.Write(65535u);             // snap length
            _writer.Write((uint)linkType);
            _writer.Flush();
        }
        catch
        {
            _stream.Dispose();                 // a failed header must not leave the file open
            throw;
        }
    }

    /// <summary>Appends one frame. Safe to call from the capture thread; not for several threads at once.</summary>
    public void Write(double time, byte[] frame)
    {
        double seconds = Math.Floor(time);
        _writer.Write((uint)seconds);
        _writer.Write((uint)((time - seconds) * 1e6));
        _writer.Write((uint)frame.Length);
        _writer.Write((uint)frame.Length);
        _writer.Write(frame);
        Frames++;
        if (++_sinceFlush >= 50)
        {
            _writer.Flush();               // a crash loses at most the last few packets
            _sinceFlush = 0;
        }
    }

    public void Dispose()
    {
        try
        {
            _writer.Flush();
        }
        finally
        {
            _writer.Dispose();                 // closed even when the flush failed (disk full, device gone)
            _stream.Dispose();
        }
    }
}
