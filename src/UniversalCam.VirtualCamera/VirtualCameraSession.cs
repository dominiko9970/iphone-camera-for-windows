using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;
using UniversalCam.Core.Video;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Main façade for the virtual camera system.
/// Probes OS version and instantiates the appropriate driver (MF or DirectShow).
/// Bridges decoded video frames and connection state to the virtual camera.
/// </summary>
public sealed class VirtualCameraSession : IDisposable
{
    private readonly PlaceholderFrameGenerator _placeholderGen = new();
    private readonly FrameBuffer _frameBuffer = new();
    private readonly SharedMemoryBridge _shmBridge = new();

    private IVirtualCameraServer? _server;
    private PeriodicTimer? _placeholderTimer;
    private int _currentWidth = 1920;
    private int _currentHeight = 1080;
    private bool _isConnected;
    private bool _isEnabled = true;
    private bool _disposed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            if (_isEnabled && _server == null)
            {
                InitializeServer();
            }
            else if (!_isEnabled)
            {
                _server?.Dispose();
                _server = null;
                _placeholderTimer?.Dispose();
                _placeholderTimer = null;
            }
        }
    }

    public VirtualCameraSession()
    {
        InitializeServer();
    }

    private void InitializeServer()
    {
        if (!_isEnabled || _disposed)
            return;

        var osVersion = Environment.OSVersion.Version;
        if (osVersion.Build >= 22621)
        {
            // MF Virtual Camera temporarily disabled — IMFPresentationDescriptor GUID mismatch
            // causes init to fail and leaves native COM event queue in unknown state.
            // TODO: fix GUID and re-enable.
            Console.WriteLine("[VirtualCamera] MF server skipped (GUID fix pending)");
        }
        else if (osVersion.Build >= 19041)
        {
            // Windows 10 21H2+: Use DirectShow
            try
            {
                _server = new DsVirtualCameraServer(_frameBuffer, _shmBridge);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VirtualCamera] Failed to initialize DirectShow server: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[VirtualCamera] OS version not supported (requires Windows 10 19041+)");
        }

        // Start placeholder timer
        if (_server != null && !_isConnected)
        {
            _placeholderTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));  // ~30fps
            _ = PushPlaceholdersAsync();
        }
    }

    /// <summary>
    /// Called when a video frame is decoded from the iPhone stream.
    /// </summary>
    public void OnFrameDecoded(object? sender, DecodedFrame frame)
    {
        if (!_isEnabled || _server == null)
            return;

        // Virtual camera only handles landscape frames; portrait trips up the COM pipeline
        if (frame.Height > frame.Width)
            return;

        // Check if resolution changed
        if (frame.Width != _currentWidth || frame.Height != _currentHeight)
        {
            _currentWidth = frame.Width;
            _currentHeight = frame.Height;
            Console.WriteLine($"[VirtualCamera] Resolution changed to {_currentWidth}×{_currentHeight}");

            // Notify servers of media type change (guard: COM can reject unusual resolutions)
            try
            {
                (_server as MfVirtualCameraServer)?.UpdateMediaType(_currentWidth, _currentHeight, 30);
                (_server as DsVirtualCameraServer)?.UpdateMediaType(_currentWidth, _currentHeight, 30);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VirtualCamera] UpdateMediaType failed for {_currentWidth}×{_currentHeight}: {ex.Message}");
            }
        }

        // Convert BGRA32 to NV12 and enqueue
        try
        {
            var nv12Data = NV12Converter.ConvertBgra32ToNv12(frame.Data, frame.Width, frame.Height);
            var nv12Frame = new NV12Frame
            {
                Width = frame.Width,
                Height = frame.Height,
                PtsUs = frame.PtsUs,
                Data = nv12Data
            };
            _frameBuffer.Enqueue(nv12Frame);
            _shmBridge.WriteFrame(nv12Frame);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VirtualCamera] Failed to process frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the connection state changes.
    /// </summary>
    public void OnConnectionStateChanged(object? sender, TransportState state)
    {
        if (!_isEnabled)
            return;

        bool wasConnected = _isConnected;
        _isConnected = state == TransportState.Connected;

        if (_isConnected && !wasConnected)
        {
            Console.WriteLine("[VirtualCamera] Connected — stopping placeholder timer");
            _placeholderTimer?.Dispose();
            _placeholderTimer = null;
        }
        else if (!_isConnected && wasConnected)
        {
            Console.WriteLine("[VirtualCamera] Disconnected — starting placeholder timer");
            _placeholderTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
            _ = PushPlaceholdersAsync();
        }
    }

    private async Task PushPlaceholdersAsync()
    {
        if (_placeholderTimer == null)
            return;

        try
        {
            while (await _placeholderTimer.WaitForNextTickAsync())
            {
                if (!_isEnabled || _server == null || _isConnected)
                    break;

                var placeholder = _placeholderGen.GenerateFrame(_currentWidth, _currentHeight);
                _frameBuffer.Enqueue(placeholder);
                _shmBridge.WriteFrame(placeholder);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when timer is disposed
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _placeholderTimer?.Dispose();
        _server?.Dispose();
        _shmBridge.Dispose();
        _placeholderGen.Dispose();
        _frameBuffer.Clear();

        _disposed = true;
    }
}

/// <summary>
/// Common interface for virtual camera server implementations.
/// </summary>
internal interface IVirtualCameraServer : IDisposable
{
    void UpdateMediaType(int width, int height, long fps);
}
