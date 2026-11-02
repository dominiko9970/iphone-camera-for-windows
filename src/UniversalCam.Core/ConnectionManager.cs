using UniversalCam.Core.Discovery;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Core;

/// <summary>
/// Central state machine for the Windows side of universal-cam.
///
/// Owns:
///   - <see cref="QuicServer"/>   — Wi-Fi transport (QUIC, port 7779)
///   - <see cref="TcpUsbTransport"/> — USB transport (TCP, port 7780)
///   - <see cref="BonjourService"/>  — mDNS advertisement so iPhone can find us
///
/// Lifecycle:
///   1. Call <see cref="StartAsync"/> — starts QUIC listener + Bonjour + USB connect attempt.
///   2. iPhone connects (either Wi-Fi or USB) → sends "hello" → we reply "welcome".
///   3. We send "configure" to set resolution/fps/codec.
///   4. iPhone sends "configure_ack" → we send "start_stream".
///   5. Media frames arrive via <see cref="FrameReceived"/>.
///   6. Call <see cref="StopStreamAsync"/> / <see cref="DisposeAsync"/> to shut down.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    // ── Events ──────────────────────────────────────────────────────────────

    /// Fires when a media frame (video or audio) is received from the iPhone.
    public event EventHandler<MediaFrame>?     FrameReceived;

    /// Fires when connection state changes (maps to underlying transport state).
    public event EventHandler<TransportState>? StateChanged;

    /// Fires when the iPhone sends its camera list.
    public event EventHandler<AvailableCameras>? CamerasReceived;

    /// Fires when the iPhone acknowledges a camera switch. Subscribers should flush decoder state.
    public event EventHandler? CameraSwitched;

    /// Fires with one-way latency (ms) after each Pong response.
    public event EventHandler<int>? LatencyUpdated;

    /// Fires when the iPhone reports a device orientation change. Value is "portrait" or "landscape".
    public event EventHandler<string>? OrientationChanged;

    // ── State ────────────────────────────────────────────────────────────────

    public TransportState State { get; private set; } = TransportState.Idle;

    /// Which transport is currently active.
    public TransportType ActiveTransport { get; private set; } = TransportType.None;

    /// Device name received from the iPhone's Hello message.
    public string DeviceName { get; private set; } = "iPhone";

    // ── Transports ───────────────────────────────────────────────────────────

    private readonly QuicServer      _quic    = new();
    private readonly TcpUsbTransport _usb     = new();
    private readonly BonjourService  _bonjour = new();

    private ITransport? _active; // points to whichever transport has the current connection

    private CancellationTokenSource? _cts;

    // ── Startup ──────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Wire both transports
        WireTransport(_quic,    TransportType.WiFi);
        WireTransport(_usb,     TransportType.USB);

        // Start QUIC listener (Wi-Fi)
        await _quic.StartAsync(_cts.Token);

        // Advertise on mDNS so iPhone BonjourDiscovery.swift can find us
        _bonjour.Start();

        // Start TCP listener for USB/TCP fallback connections from iPhone
        await _usb.ConnectAsync(_cts.Token);

        SetState(TransportState.Listening);
    }

    // ── Control ──────────────────────────────────────────────────────────────

    /// Send a configure message to the connected iPhone.
    public Task ConfigureAsync(
        string resolution = "1080p",
        int    fps        = 30,
        string codec      = "h264",
        int    bitrate    = 8_000_000,
        CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        return _active.SendControlAsync(
            new Configure { Resolution = resolution, Fps = fps, Codec = codec, Bitrate = bitrate }, ct);
    }

    /// Tell the iPhone to start streaming.
    public Task StartStreamAsync(CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        return _active.SendControlAsync(new StartStream(), ct);
    }

    /// Tell the iPhone to stop streaming.
    public Task StopStreamAsync(CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        return _active.SendControlAsync(new StopStream(), ct);
    }

    /// Ask the iPhone for its camera list.
    public Task ListCamerasAsync(CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        return _active.SendControlAsync(new ListCameras(), ct);
    }

    /// Tell the iPhone to switch to a specific camera by ID.
    public Task SwitchCameraAsync(string cameraId, CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        return _active.SendControlAsync(new SwitchCamera { CameraId = cameraId }, ct);
    }

    /// Send a ping to measure round-trip latency.
    public Task PingAsync(CancellationToken ct = default)
    {
        if (_active is null) return Task.CompletedTask;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _active.SendControlAsync(new Ping { Ts = ts }, ct);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private int _frameCount;

    private void OnFrameReceived(TransportType type, MediaFrame frame)
    {
        int n = System.Threading.Interlocked.Increment(ref _frameCount);
        if (n == 1 || n % 150 == 0)
            Console.WriteLine($"[ConnectionManager] {type} frame #{n}: {(frame.IsVideo ? "video" : "audio")} {frame.Payload.Length} bytes keyframe={frame.Header.IsKeyframe}");
        FrameReceived?.Invoke(this, frame);
    }

    private void WireTransport(ITransport transport, TransportType type)
    {
        transport.StateChanged          += (_, s) => OnTransportStateChanged(transport, type, s);
        transport.FrameReceived         += (_, f) => OnFrameReceived(type, f);
        transport.ControlMessageReceived += (_, m) => OnControlMessage(transport, type, m);
    }

    private void OnTransportStateChanged(ITransport transport, TransportType type, TransportState state)
    {
        Console.WriteLine($"[ConnectionManager] {type} → {state}");

        switch (state)
        {
            // USB uses Welcome→Hello: send Welcome so iOS responds with Hello.
            // WiFi (QUIC) sends Hello proactively when ready, so no Welcome needed there.
            case TransportState.Connected:
                if (type == TransportType.USB)
                    _ = transport.SendControlAsync(new Welcome());
                break;

            case TransportState.Disconnected:
            case TransportState.Error:
                if (_active == transport)
                {
                    _active         = null;
                    ActiveTransport = TransportType.None;
                    SetState(TransportState.Listening);
                }
                break;
        }
    }

    private void OnControlMessage(ITransport transport, TransportType type, ControlMessage msg)
    {
        if (msg is not Ping and not Pong)
            Console.WriteLine($"[ConnectionManager] {type} control: {msg.Type}");

        switch (msg)
        {
            case Hello hello:
                Console.WriteLine($"  Device: {hello.DeviceName}, capabilities: [{string.Join(", ", hello.Capabilities)}]");
                DeviceName = hello.DeviceName;

                // USB always wins over WiFi; WiFi only wins if nothing connected yet.
                // Same-transport reconnects (e.g., iOS closed and re-opened the QUIC connection)
                // are always promoted so Windows re-sends Configure and streaming resumes.
                bool shouldPromote = _active is null
                    || (ActiveTransport == TransportType.WiFi && type == TransportType.USB)
                    || ActiveTransport == type;
                if (shouldPromote)
                {
                    _active         = transport;
                    ActiveTransport = type;
                    SetState(TransportState.Connected);
                }
                break;

            case ConfigureAck ack:
                Console.WriteLine($"  Configure ack: {ack.Resolution} @ {ack.Fps}fps");
                // Immediately start stream after configure acknowledged
                _ = transport.SendControlAsync(new StartStream());
                break;

            case SwitchCameraAck:
                CameraSwitched?.Invoke(this, EventArgs.Empty);
                break;

            case AvailableCameras cameras:
                CamerasReceived?.Invoke(this, cameras);
                break;

            case Pong pong:
                var rtt = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.Ts);
                LatencyUpdated?.Invoke(this, rtt / 2);
                break;

            case Ping ping:
                // Respond so iOS can measure its round-trip latency
                _ = transport.SendControlAsync(new Pong { Ts = ping.Ts });
                break;

            case Protocol.OrientationChanged oc:
                OrientationChanged?.Invoke(this, oc.Orientation);
                break;
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
        await StopStreamAsync();
        await _bonjour.DisposeAsync();
        await _quic.DisposeAsync();
        await _usb.DisposeAsync();
    }
}

public enum TransportType { None, WiFi, USB }
