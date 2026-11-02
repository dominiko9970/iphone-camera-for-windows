// DirectShow Integration Guide for UniversalCam Virtual Camera
// This file documents the integration pattern for Phase 3 (DirectShow DLL for Windows 10+)

/*
===============================================================================
PHASE 3: DirectShow Filter DLL Build & SharedMemoryBridge Integration
===============================================================================

PREREQUISITES:
- Visual Studio 2022 with "Desktop development with C++" workload
- Windows SDK (for DirectShow headers)
- Platform Toolset v143 or later

BUILD STEPS:
1. Open Universal-Cam.sln in Visual Studio 2022
2. Select "Release|x64" configuration
3. Right-click UniversalCam.VirtualCamera.DirectShow project → Build
4. Verify UniversalCamVCam.dll appears in: src/UniversalCam/bin/x64/Release/
5. Register filter with: regsvr32 src/UniversalCam/bin/x64/Release/UniversalCamVCam.dll
   (May require UAC elevation)

===============================================================================
IMPLEMENTATION: SharedMemoryBridge Integration
===============================================================================

The DirectShow filter reads NV12 frames from the named MemoryMappedFile 
created by the C# VirtualCameraSession. This allows IPC between managed code 
and the unmanaged DirectShow DLL.

Key Components:
================
1. SharedMemoryBridge.cs (C#)
   - Writes frame data to named MMF: "UniversalCam_VCam_Frame"
   - Header format (14 bytes):
     * uint32 width
     * uint32 height  
     * uint32 stride
     * uint32 dataSize (width × height × 1.5 for NV12)
     * uint32 sequenceNo
     * uint32 ptsUs (presentation timestamp in microseconds)
   
   - Followed by NV12 frame data:
     * Y plane: width × height bytes
     * UV plane: (width/2) × (height/2) × 2 bytes (interleaved U/V)

2. UCamCapturePin.cpp (C++)
   - Derives from CSourceStream (DirectShow base class)
   - Override FillBuffer() to read from MMF instead of filling black
   - Translate MMF timestamps to DirectShow REFERENCE_TIME format

===============================================================================
TODO IMPLEMENTATION: Read from SharedMemoryBridge in FillBuffer()
===============================================================================

Replace the TODO section in UCamCapturePin::FillBuffer() with:

--BEGIN CODE SNIPPET--

// Step 1: Open/map the SharedMemoryBridge MMF
// Note: Use RAII pattern; consider caching MMF handle and view in member vars
// to avoid repeated open/close on each frame

HANDLE hMapFile = nullptr;
void *pMapView = nullptr;

try {
    // Open the named memory-mapped file created by C# VirtualCameraSession
    hMapFile = OpenFileMappingW(FILE_MAP_READ, FALSE, L"UniversalCam_VCam_Frame");
    if (hMapFile == nullptr) {
        // MMF not available (C# app not running or not created yet)
        // Fall through to black placeholder
        throw std::runtime_error("SharedMemoryBridge MMF not found");
    }

    pMapView = MapViewOfFile(hMapFile, FILE_MAP_READ, 0, 0, 0);
    if (pMapView == nullptr) {
        throw std::runtime_error("MapViewOfFile failed");
    }

    // Step 2: Read frame header
    const int HEADER_SIZE = 14 * sizeof(DWORD); // 56 bytes
    const BYTE *pHeader = (const BYTE *)pMapView;
    
    DWORD frameWidth = *(const DWORD *)(pHeader + 0);
    DWORD frameHeight = *(const DWORD *)(pHeader + 4);
    DWORD frameStride = *(const DWORD *)(pHeader + 8);
    DWORD frameDataSize = *(const DWORD *)(pHeader + 12);
    DWORD frameSeqNo = *(const DWORD *)(pHeader + 16);
    LONGLONG framePtsUs = *(const LONGLONG *)(pHeader + 20);
    
    // Validate frame dimensions
    if (frameWidth == 0 || frameHeight == 0 || frameDataSize == 0) {
        throw std::runtime_error("Invalid frame header");
    }
    
    // Validate frame data size
    int expectedSize = frameWidth * frameHeight * 3 / 2; // NV12 format
    if (frameDataSize != expectedSize) {
        throw std::runtime_error("Frame size mismatch");
    }

    // Step 3: Copy NV12 frame data to IMediaSample
    BYTE *pData = nullptr;
    HRESULT hr = pms->GetPointer(&pData);
    if (FAILED(hr)) {
        throw std::runtime_error("GetPointer failed");
    }

    const BYTE *pFrameData = pHeader + HEADER_SIZE;
    CopyMemory(pData, pFrameData, frameDataSize);
    pms->SetActualDataLength(frameDataSize);

    // Step 4: Translate presentation timestamp
    // PtsUs from C# = microseconds
    // DirectShow REFERENCE_TIME = 100-nanosecond units
    // Conversion: microseconds × 10 = 100ns units
    LONGLONG rtStart = framePtsUs * 10;
    LONGLONG rtStop = rtStart + m_videoInfo.AvgTimePerFrame;
    
    pms->SetTime(&rtStart, &rtStop);
    m_rtLastSampleTime = rtStop;

    // Step 5: Update media type if resolution changed
    if (frameWidth != m_videoInfo.bmiHeader.biWidth ||
        frameHeight != m_videoInfo.bmiHeader.biHeight) {
        
        // Log resolution change
        OutputDebugStringW(
            std::wstring(L"[UCamCapturePin] Resolution changed: ") +
            std::to_wstring(frameWidth) + L"x" +
            std::to_wstring(frameHeight) + L"\r\n"
        ).c_str()
        );
        
        // Update cached video info for next frame
        m_videoInfo.bmiHeader.biWidth = frameWidth;
        m_videoInfo.bmiHeader.biHeight = frameHeight;
        m_videoInfo.bmiHeader.biSizeImage = frameDataSize;
    }

    pms->SetSyncPoint(TRUE);
    m_dwFrameCount++;

    return S_OK;

} catch (const std::exception &ex) {
    OutputDebugStringA(
        std::string("[UCamCapturePin] Frame read failed: ") + ex.what() + "\r\n"
    ).c_str()
    );
    
    // Fall through to black placeholder on error
}

finally {
    // Clean up MMF mapping
    if (pMapView != nullptr) {
        UnmapViewOfFile(pMapView);
    }
    if (hMapFile != nullptr) {
        CloseHandle(hMapFile);
    }
}

// Fallback: Fill with black (Y=0, U=128, V=128)
BYTE *pData = nullptr;
long cbData = pms->GetSize();
HRESULT hr = pms->GetPointer(&pData);
if (FAILED(hr))
    return hr;

ZeroMemory(pData, cbData);
int ySize = m_videoInfo.bmiHeader.biWidth * m_videoInfo.bmiHeader.biHeight;
FillMemory(pData + ySize, cbData - ySize, 0x80);

pms->SetActualDataLength(cbData);
LONGLONG rtStart = m_rtLastSampleTime;
LONGLONG rtStop = rtStart + m_videoInfo.AvgTimePerFrame;
pms->SetTime(&rtStart, &rtStop);
m_rtLastSampleTime = rtStop;
pms->SetSyncPoint(TRUE);

return S_OK;

--END CODE SNIPPET--

===============================================================================
OPTIMIZATION: Persistent MMF Mapping
===============================================================================

For production: Cache the MMF handle and view in member variables to avoid
repeated open/close on each frame. Example:

class UCamCapturePin : public CSourceStream {
private:
    HANDLE m_hMapFile = nullptr;
    BYTE *m_pMapView = nullptr;
    CRITICAL_SECTION m_mfMutex;
    
    void InitializeSHMBridge() {
        EnterCriticalSection(&m_mfMutex);
        if (m_hMapFile == nullptr) {
            m_hMapFile = OpenFileMappingW(FILE_MAP_READ, FALSE, L"UniversalCam_VCam_Frame");
            if (m_hMapFile != nullptr) {
                m_pMapView = (BYTE *)MapViewOfFile(m_hMapFile, FILE_MAP_READ, 0, 0, 0);
            }
        }
        LeaveCriticalSection(&m_mfMutex);
    }
    
    void CleanupSHMBridge() {
        EnterCriticalSection(&m_mfMutex);
        if (m_pMapView != nullptr) {
            UnmapViewOfFile(m_pMapView);
            m_pMapView = nullptr;
        }
        if (m_hMapFile != nullptr) {
            CloseHandle(m_hMapFile);
            m_hMapFile = nullptr;
        }
        LeaveCriticalSection(&m_mfMutex);
    }
};

===============================================================================
TESTING: Verification Checklist
===============================================================================

After building and registering the DirectShow filter:

1. Windows 10 Camera List
   [ ] Device enumeration: Windows 10 Settings → Devices → Cameras
   [ ] Should see "UniversalCam Virtual Camera" in list
   
2. Browser (Chrome, Edge)
   [ ] Open camera test: https://webcamtests.com/
   [ ] Select "UniversalCam Virtual Camera"
   [ ] Verify feed displays (should show placeholder initially)
   
3. Teams
   [ ] Settings → Audio devices → Camera
   [ ] Select "UniversalCam Virtual Camera"
   [ ] Join call, verify preview shows
   
4. OBS Studio
   [ ] Sources → Video Capture Device
   [ ] Select "UniversalCam Virtual Camera"
   [ ] Verify source preview updates with placeholder
   
5. Connect iPhone
   [ ] Launch UniversalCam desktop app
   [ ] Connect iPhone via QUIC
   [ ] Verify real camera frame appears in browser/Teams/OBS
   [ ] Resolution changes detected (desktop resizes camera feed)
   [ ] Disconnect iPhone → placeholder displays

===============================================================================
TROUBLESHOOTING
===============================================================================

Issue: "Device not enumerated"
→ Verify regsvr32 succeeded (check HKLM\Software\Classes\CLSID\{12345678-...}\InprocServer32)
→ Restart applications that check camera list on startup
→ Run regsvr32 with /u to unregister first, then re-register

Issue: "Filter loads but no frames"
→ Verify C# VirtualCameraSession is running (check Event Viewer → Applications)
→ Check MMF is created: Handle debugger or ProcessExplorer to inspect
→ Add OutputDebugStringW() calls in UCamCapturePin::FillBuffer() and debug in VS

Issue: "Corrupted frames / flickering"
→ Check frame size calculation: width × height × 3/2 must match C# side
→ Verify byte-order (little-endian) in header reads
→ Ensure thread-safety: lock MMF access if multiple frames in flight

===============================================================================
FUTURE ENHANCEMENTS
===============================================================================

1. Resolution negotiation: Allow DirectShow filter to request specific resolution
   → Add control interface (IAMVfwCaptureDialogs) to configure width/height/fps

2. Frame rate control: Support 15fps, 24fps, 30fps selections

3. Format negotiation: Support MJPEG, H264 in addition to NV12 (via separate pins)

4. Performance profiling: Add timestamp-based latency measurements

===============================================================================
*/
