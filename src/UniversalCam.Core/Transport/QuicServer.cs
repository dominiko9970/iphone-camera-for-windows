#pragma warning disable CA2252  // System.Net.Quic is preview in .NET 8; suppressed project-wide here
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>
/// QUIC server (Wi-Fi transport). Listens on port 7779 for incoming connections
/// from the iPhone's QuicTransport.swift.
///
/// Uses System.Net.Quic (built into .NET 8) with a self-signed TLS certificate
/// for development. The iPhone's QuicTransport accepts any cert in DEBUG builds.
///
/// ALPN: "universalcam/1" — must match the iPhone's NWProtocolQUIC.Options(alpn:).
/// </summary>
public sealed class QuicServer : ITransport
{
    public const int Port = 7779;
    private const string Alpn = "universalcam/1";

    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<MediaFrame>?     FrameReceived;
    public event EventHandler<TransportState>? StateChanged;

    public TransportState State { get; private set; } = TransportState.Idle;

    private QuicListener?   _listener;
    private QuicConnection? _connection;
    private QuicStream?     _responseStream; // the bidirectional stream iOS opened; we write back on it
    private CancellationTokenSource? _cts;
    // Serialize writes: QuicStream doesn't allow concurrent WriteAsync/FlushAsync calls.
    // Configure (UI thread) and StartStream (QUIC receive thread) can overlap otherwise.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!QuicListener.IsSupported)
        {
            Console.WriteLine("[QuicServer] QUIC not supported on this platform. Falling back to TCP.");
            SetState(TransportState.Error);
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var cert = CreateSelfSignedCert();

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint           = new IPEndPoint(IPAddress.Any, Port),
            ApplicationProtocols     = new List<SslApplicationProtocol> { new(Alpn) },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode    = 0,
                DefaultCloseErrorCode     = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols     = new List<SslApplicationProtocol> { new(Alpn) },
                    ServerCertificate        = cert,
                    ClientCertificateRequired = false,
                },
            }),
        };

        _listener = await QuicListener.ListenAsync(listenerOptions, _cts.Token);
        SetState(TransportState.Listening);
        Console.WriteLine($"[QuicServer] Listening on port {Port} (ALPN: {Alpn})");

        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task SendControlAsync(ControlMessage message, CancellationToken ct = default)
    {
        // Fast pre-check (no lock): skip if obviously no stream.
        if (_responseStream is null) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check INSIDE the lock — AcceptLoopAsync also holds _writeLock when it
            // disposes _responseStream, so this check is race-free.
            var stream = _responseStream;
            if (stream is null) return;

            var json = message.ToJsonBytes();
            var packet = new byte[json.Length + 2];
            packet[0] = FrameHeader.StreamTypeControl;
            json.CopyTo(packet, 1);
            packet[^1] = 0x0A;
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuicServer] Send failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // MARK: - Private

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener is not null)
            {
                var connection = await _listener.AcceptConnectionAsync(ct);
                Console.WriteLine($"[QuicServer] iPhone connected: {connection.RemoteEndPoint}");

                // Hold _writeLock while swapping out _responseStream so SendControlAsync
                // cannot capture then write to a stream we're about to dispose.
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_responseStream is not null)
                    {
                        try { await _responseStream.DisposeAsync(); } catch { }
                        _responseStream = null;
                    }
                }
                finally { _writeLock.Release(); }

                // Fire-and-forget close: don't block accepting the new connection's streams.
                // iOS sends Hello immediately on connect; if we awaited CloseAsync here,
                // ReadStreamsAsync for the new connection wouldn't start until the old one
                // finished closing, causing iOS to time out and reconnect in a loop.
                _connection?.CloseAsync(0).AsTask().Forget();
                _connection = connection;

                SetState(TransportState.Connected);

                // iOS sends Hello on a bidirectional stream immediately on .ready.
                // We accept that stream and write Configure/StartStream back on it.
                _ = ReadStreamsAsync(connection, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuicServer] Accept error: {ex.Message}");
            SetState(TransportState.Error);
        }
    }

    private async Task ReadStreamsAsync(QuicConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(ct);
                // Always update to the latest writable (bidirectional) stream iOS opened.
                // iOS may open a new control stream after a soft reconnect; we need to
                // reply on whichever stream it's currently listening to.
                if (stream.CanWrite)
                {
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try { _responseStream = stream; }
                    finally { _writeLock.Release(); }
                }
                // Each QUIC stream gets its own FrameParser to avoid concurrent access on
                // the shared buffer — iOS opens separate streams for control and media,
                // and concurrent List<byte>.AddRange calls from multiple ReadStreamAsync
                // tasks would corrupt the buffer, causing CLR crash 0x80131506 and dropped frames.
                var parser = new FrameParser();
                parser.ControlMessageParsed += (_, msg) => ControlMessageReceived?.Invoke(this, msg);
                parser.FrameParsed          += (_, frame) => FrameReceived?.Invoke(this, frame);
                _ = ReadStreamAsync(stream, parser, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuicServer] Stream accept error: {ex.Message}");
            if (connection == _connection)
                SetState(TransportState.Disconnected);
        }
    }

    private async Task ReadStreamAsync(QuicStream stream, FrameParser parser, CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                parser.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuicServer] Stream read error: {ex.Message}");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=universalcam-dev",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        // Export and re-import with private key to make it usable for TLS
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }

    private void SetState(TransportState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_responseStream is not null) await _responseStream.DisposeAsync();
        if (_connection    is not null) await _connection.CloseAsync(0);
        if (_listener      is not null) await _listener.DisposeAsync();
    }
}

// Small helper to fire-and-forget tasks without warning CS4014
file static class TaskExtensions
{
    public static void Forget(this Task _) { }
}
