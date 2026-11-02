using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Windows 10+ virtual camera server using DirectShow.
/// Requires a native C++ DLL (UniversalCam.VirtualCamera.DirectShow.dll) registered via regsvr32.
/// Uses SharedMemoryBridge to communicate frame data with the DLL.
/// </summary>
internal sealed class DsVirtualCameraServer : IVirtualCameraServer
{
    private readonly FrameBuffer _frameBuffer;
    private readonly SharedMemoryBridge _shmBridge;
    private bool _dllRegistered;
    private bool _disposed;

    private const string DllFileName = "UniversalCamVCam.dll";
    private const string RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\CLSID";
    private const string FilterClsid = "{12345678-1234-1234-1234-123456789012}";  // TODO: Update with actual CLSID from C++ DLL

    public DsVirtualCameraServer(FrameBuffer frameBuffer, SharedMemoryBridge shmBridge)
    {
        _frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));
        _shmBridge = shmBridge ?? throw new ArgumentNullException(nameof(shmBridge));
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // Check if the DirectShow filter DLL is already registered
            _dllRegistered = CheckDllRegistration();

            if (!_dllRegistered)
            {
                Console.WriteLine("[DsVirtualCameraServer] DirectShow DLL not registered. Attempting registration...");
                if (TryRegisterDll())
                {
                    _dllRegistered = true;
                    Console.WriteLine("[DsVirtualCameraServer] DLL registration successful");
                }
                else
                {
                    Console.WriteLine("[DsVirtualCameraServer] DLL registration failed or requires UAC elevation");
                }
            }
            else
            {
                Console.WriteLine("[DsVirtualCameraServer] DirectShow DLL already registered");
            }

            Console.WriteLine("[DsVirtualCameraServer] Initialized (Windows 10+)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DsVirtualCameraServer] Initialization error: {ex.Message}");
        }
    }

    private bool CheckDllRegistration()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\CLSID\{FilterClsid}"))
            {
                return key != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private bool TryRegisterDll()
    {
        try
        {
            // Find the DLL in the application directory
            string appDir = AppContext.BaseDirectory;
            string dllPath = Path.Combine(appDir, DllFileName);

            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"[DsVirtualCameraServer] DLL not found at {dllPath}");
                return false;
            }

            // Try to register without elevation first (may fail on some systems)
            var psi = new ProcessStartInfo
            {
                FileName = "regsvr32.exe",
                Arguments = $"/s \"{dllPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas"  // Request UAC elevation
            };

            using (var proc = Process.Start(psi))
            {
                proc?.WaitForExit(5000);  // Wait up to 5 seconds
                return proc?.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DsVirtualCameraServer] DLL registration error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Updates the frame type when resolution/fps changes.
    /// </summary>
    public void UpdateMediaType(int width, int height, long fps)
    {
        // Write a frame to the MMF so the DLL knows the current format
        // TODO: Once DirectShow DLL is implemented, it will read these values and adjust IAMStreamConfig
        Console.WriteLine($"[DsVirtualCameraServer] Media type updated: {width}×{height} @ {fps}fps");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            // DirectShow DLL will continue running and reading from SharedMemoryBridge
            // No explicit stop is needed unless we want to deregister the filter
            Console.WriteLine("[DsVirtualCameraServer] Disposed");
        }
        finally
        {
            _disposed = true;
        }
    }
}
