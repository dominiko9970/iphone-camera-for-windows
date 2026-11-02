using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Windows 11 22H2+ virtual camera server using Media Foundation IMFVirtualCamera.
/// Registers the app as a camera device without requiring driver signing.
/// </summary>
internal sealed class MfVirtualCameraServer : IVirtualCameraServer
{
    private readonly FrameBuffer _frameBuffer;
    private MfMediaSourceWrapper? _mediaSourceWrapper;
    private IMFVirtualCamera? _virtualCamera;
    private bool _mfStartupCalled;
    private bool _isRunning;
    private bool _disposed;
    private Task? _frameDeliveryTask;
    private CancellationTokenSource? _cts;

    public MfVirtualCameraServer(FrameBuffer frameBuffer)
    {
        _frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            Console.WriteLine("[MfVirtualCameraServer] Initializing (Windows 11 22H2+)");

            // MFStartup must be called before any other MF API (including MFCreateEventQueue)
            if (!InitializeMediaFoundation())
                return;

            // Now safe to create the media source (calls MFCreateEventQueue internally)
            _mediaSourceWrapper = new MfMediaSourceWrapper(_frameBuffer);

            if (!CreateVirtualCamera())
                return;

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _frameDeliveryTask = FrameDeliveryLoop(_cts.Token);

            Console.WriteLine("[MfVirtualCameraServer] Initialization complete — virtual camera is live");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] Initialization failed: {ex.Message}");
            _isRunning = false;
            // Shut down + clean up any partially-created MF objects so their COM threads
            // don't fire callbacks into this half-initialized instance and crash the CLR.
            // Shutdown() cancels pending BeginGetEvent callbacks before Dispose releases the COM refs.
            try { ((IMFMediaSource?)_mediaSourceWrapper)?.Shutdown(); } catch { }
            try { _mediaSourceWrapper?.Dispose(); } catch { }
            _mediaSourceWrapper = null;
            if (_mfStartupCalled) { try { NativeMF.MFShutdown(); } catch { } _mfStartupCalled = false; }
            throw; // let VirtualCameraSession catch this so _server stays null
        }
    }

    private bool InitializeMediaFoundation()
    {
        try
        {
            int hr = NativeMF.MFStartup(NativeMF.MF_VERSION, NativeMF.MFSTARTUP_LITE);
            if (!NativeMF.Succeeded(hr))
            {
                Console.WriteLine($"[MfVirtualCameraServer] MFStartup failed: 0x{hr:X8}");
                return false;
            }
            _mfStartupCalled = true;
            Console.WriteLine("[MfVirtualCameraServer] MFStartup succeeded");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] Media Foundation DLL not found: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] MFStartup error: {ex.Message}");
            return false;
        }
    }

    private bool CreateVirtualCamera()
    {
        try
        {
            int hr = NativeMF.MFCreateVirtualCamera(
                NativeMF.MFVirtualCamera_Software,
                NativeMF.MFVirtualCamera_Session,
                NativeMF.MFVirtualCamera_AllUsers,
                "UniversalCam",
                _mediaSourceWrapper!,
                IntPtr.Zero,
                out _virtualCamera);

            if (!NativeMF.Succeeded(hr) || _virtualCamera == null)
            {
                Console.WriteLine($"[MfVirtualCameraServer] MFCreateVirtualCamera failed: 0x{hr:X8}");
                return false;
            }

            // Start the camera — this registers it in the Windows device list
            _virtualCamera.Start(_mediaSourceWrapper);
            Console.WriteLine("[MfVirtualCameraServer] Virtual camera created and started — visible in Settings > Cameras");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] mfvirtualcamera.dll not found (requires Windows 11 22H2+): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] CreateVirtualCamera error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Runs at ~30 fps; logs periodic heartbeat so you can confirm frames are flowing.
    /// Actual sample delivery happens via IMFMediaStream.RequestSample() called by MF.
    /// </summary>
    private async Task FrameDeliveryLoop(CancellationToken ct)
    {
        int frameCount = 0;
        try
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                // Just count enqueued frames — MF drives RequestSample() itself
                int buffered = _frameBuffer.Count;
                if (buffered > 0)
                {
                    frameCount++;
                    if (frameCount % 150 == 0)  // log every ~5 seconds at 30 fps
                        Console.WriteLine($"[MfVirtualCameraServer] Frame delivery active — buffer: {buffered}");
                }

                await Task.Delay(33, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.WriteLine($"[MfVirtualCameraServer] Frame delivery loop ended");
        }
    }

    // ── IVirtualCameraServer ───────────────────────────────────────────────

    public void UpdateMediaType(int width, int height, long fps)
    {
        try
        {
            _mediaSourceWrapper?.UpdateMediaType(width, height, fps);
            Console.WriteLine($"[MfVirtualCameraServer] Media type updated: {width}×{height} @ {fps}fps");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] UpdateMediaType error: {ex.Message}");
        }
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _isRunning = false;
            _cts?.Cancel();
            _frameDeliveryTask?.Wait(TimeSpan.FromSeconds(2));

            if (_virtualCamera != null)
            {
                try { _virtualCamera.Stop(); }  catch { /* best-effort */ }
                try { _virtualCamera.Remove(); } catch { /* best-effort */ }
                Marshal.ReleaseComObject(_virtualCamera);
                _virtualCamera = null;
            }

            if (_mfStartupCalled)
            {
                NativeMF.MFShutdown();
                _mfStartupCalled = false;
            }

            _mediaSourceWrapper?.Dispose();
            Console.WriteLine("[MfVirtualCameraServer] Disposed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfVirtualCameraServer] Dispose error: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _disposed = true;
        }
    }
}
