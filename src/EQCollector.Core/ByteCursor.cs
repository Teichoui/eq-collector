using System.Text;

namespace EQCollector.Core;

/// <summary>A record did not parse -- almost always because decoding started at the wrong place.</summary>
public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}

/// <summary>Reads little-endian values from a byte array, throwing <see cref="ParseException"/> instead of running past the end.</summary>
public sealed class ByteCursor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly byte[] _data;
    private int _pos;

    public ByteCursor(byte[] data, int position = 0)
    {
        _data = data;
        _pos = position;
    }

    public int Position => _pos;
    public int Left => _data.Length - _pos;

    private void Need(int n)
    {
        if (n < 0 || n > _data.Length - _pos) throw new ParseException("ran out of data");   // written so a huge n cannot overflow the sum
    }

    public void Skip(int n)
    {
        Need(n);
        _pos += n;
    }

    public byte Peek(int offset)
    {
        int i = _pos + offset;
        if (i < 0 || i >= _data.Length) throw new ParseException("ran out of data");
        return _data[i];
    }

    public byte U8() { Need(1); return _data[_pos++]; }
    public sbyte I8() { Need(1); return unchecked((sbyte)_data[_pos++]); }

    public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_data, _pos); _pos += 2; return v; }
    public uint U32() { Need(4); var v = BitConverter.ToUInt32(_data, _pos); _pos += 4; return v; }
    public int I32() { Need(4); var v = BitConverter.ToInt32(_data, _pos); _pos += 4; return v; }
    public long I64() { Need(8); var v = BitConverter.ToInt64(_data, _pos); _pos += 8; return v; }

    /// <summary>A 32-bit float, rounded to six places (the way the values read in the tooltips).</summary>
    public double F32()
    {
        Need(4);
        var v = BitConverter.ToSingle(_data, _pos);
        _pos += 4;
        return Math.Round((double)v, 6);
    }

    /// <summary>A null-terminated string (stopping early at <paramref name="maxLength"/> when given). An undecodable string reads as "".</summary>
    public string ReadString(int maxLength = 0)
    {
        int start = _pos;
        int end = start;
        while (end < _data.Length && _data[end] != 0 && (maxLength == 0 || end - start < maxLength)) end++;

        // step over the terminator when there is one
        _pos = end < _data.Length && _data[end] == 0 ? end + 1 : end;
        if (end == start) return "";
        try
        {
            return StrictUtf8.GetString(_data, start, end - start);
        }
        catch (DecoderFallbackException)
        {
            return "";
        }
    }
}
