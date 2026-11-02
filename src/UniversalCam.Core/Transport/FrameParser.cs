using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>
/// Parses the binary frame stream shared by both QuicTransport and TcpUsbTransport.
///
/// The stream is a series of:
///   [1 byte stream_type][4 bytes length LE][8 bytes pts_us LE][1 byte flags][N bytes payload]
///
/// Control frames (stream_type = 0x00) are newline-delimited JSON.
/// Video frames (0x01) and audio frames (0x02) use the binary layout.
/// </summary>
public sealed class FrameParser
{
    private readonly List<byte> _buffer = new(capacity: 65536);

    public event EventHandler<ControlMessage>? ControlMessageParsed;
    public event EventHandler<MediaFrame>?     FrameParsed;

    /// Feed raw bytes from the transport into the parser.
    public void Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        Parse();
    }

    public void Reset() => _buffer.Clear();

    private void Parse()
    {
        while (_buffer.Count > 0)
        {
            var streamType = _buffer[0];

            if (streamType == FrameHeader.StreamTypeControl)
            {
                // Control: find newline delimiter
                int nlIndex = _buffer.IndexOf(0x0A, startIndex: 1);
                if (nlIndex < 0) return; // Incomplete — wait for more data

                // JSON is bytes [1..nlIndex-1]
                var jsonBytes = _buffer.Skip(1).Take(nlIndex - 1).ToArray();
                _buffer.RemoveRange(0, nlIndex + 1);

                var msg = ControlMessage.FromJson(jsonBytes);
                if (msg is not null)
                    ControlMessageParsed?.Invoke(this, msg);
            }
            else if (streamType == FrameHeader.StreamTypeVideo ||
                     streamType == FrameHeader.StreamTypeAudio)
            {
                // Binary frame: need at least FrameHeader.Size bytes
                if (_buffer.Count < FrameHeader.Size) return;

                var headerSpan = _buffer.Take(FrameHeader.Size).ToArray().AsSpan();
                if (!FrameHeader.TryParse(headerSpan, out var header)) return;

                var totalNeeded = FrameHeader.Size + (int)header.Length;
                if (_buffer.Count < totalNeeded) return; // Incomplete payload

                var payload = _buffer.Skip(FrameHeader.Size).Take((int)header.Length).ToArray();
                _buffer.RemoveRange(0, totalNeeded);

                FrameParsed?.Invoke(this, new MediaFrame(header, payload));
            }
            else
            {
                // Unknown stream type — discard byte and resync
                _buffer.RemoveAt(0);
            }
        }
    }
}

file static class ListExtensions
{
    public static int IndexOf(this List<byte> list, byte value, int startIndex)
    {
        for (int i = startIndex; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
