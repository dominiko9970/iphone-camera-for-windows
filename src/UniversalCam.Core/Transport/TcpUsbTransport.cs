using System.Net;
using System.Net.Sockets;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>
/// TCP server transport — listens on port 7780 for incoming connections from the iPhone.
///
/// Works both over USB (when Apple Mobile Device Service creates a virtual network
/// adapter) and over plain Wi-Fi as a QUIC fallback.  The iPhone app connects to
/// the PC's IP address on this port via its TCPTransport.
///
/// Wire protocol is identical to QuicTransport — same frame layout, same control JSON.
/// </summary>
public sealed class TcpUsbTransport : ITransport
{
    public const int Port = 7780;

    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<MediaFrame>?     FrameReceived;
    public event EventHandler<TransportState>? StateChanged;

    public TransportState State { get; private set; } = TransportState.Idle;

    private TcpListener?      _listener;
    private TcpClient?        _client;
    private NetworkStream?    _stream;
    private readonly FrameParser _parser = new();
    private CancellationTokenSource? _cts;

    public TcpUsbTransport()
    {
        _parser.ControlMessageParsed += (_, msg) => ControlMessageReceived?.Invoke(this, msg);
        _parser.FrameParsed          += (_, frame) => FrameReceived?.Invoke(this, frame);
    }

    /// Start listening for incoming TCP connections on port 7780.
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_listener is not null) return; // already listening

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            SetState(TransportState.Listening);
            Console.WriteLine($"[TcpUsbTransport] Listening on TCP port {Port}");

            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TcpUsbTransport] Failed to start listener: {ex.Message}");
            SetState(TransportState.Error);
        }

        await Task.CompletedTask;
    }

    public async Task SendControlAsync(ControlMessage message, CancellationToken ct = default)
    {
        if (_stream is null) return;
        try
        {
            var json = message.ToJsonBytes();
            var packet = new byte[json.Length + 2];
            packet[0] = FrameHeader.StreamTypeControl;
            json.CopyTo(packet, 1);
            packet[^1] = 0x0A;
            await _stream.WriteAsync(packet, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TcpUsbTransport] Send failed: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                Console.WriteLine($"[TcpUsbTransport] iPhone connected: {client.Client.RemoteEndPoint}");

                // Drop existing connection if any
                _client?.Close();
                _stream?.Dispose();

                _client = client;
                _stream = client.GetStream();
                SetState(TransportState.Connected);
                _parser.Reset();

                // ConnectionManager sends Welcome on the Connected event.
                // Do not send a second concurrent greeting on this stream.

                _ = ReceiveLoopAsync(_stream, _cts!.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[TcpUsbTransport] Accept error: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                _parser.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[TcpUsbTransport] Receive error: {ex.Message}");
        }
        finally
        {
            if (_stream == stream) // only update state if this is still the active stream
            {
                SetState(TransportState.Disconnected);
                _parser.Reset();
                Console.WriteLine("[TcpUsbTransport] iPhone disconnected — still listening for reconnect");
            }
        }
    }

    private void SetState(TransportState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Close();
        _listener?.Stop();
        await Task.CompletedTask;
    }
}
