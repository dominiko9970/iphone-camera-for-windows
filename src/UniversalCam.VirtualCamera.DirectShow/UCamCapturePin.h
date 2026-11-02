#pragma once

class UCamCapturePin : public CSourceStream
{
public:
    UCamCapturePin(HRESULT *phr, CSource *pFilter);
    ~UCamCapturePin();

    // Implement IMediaSeeking (empty, since we're live)
    HRESULT CheckMediaType(const CMediaType *pMediaType);
    HRESULT GetMediaType(int iPosition, CMediaType *pmt);
    HRESULT DecideBufferSize(IMemAllocator *pAlloc, ALLOCATOR_PROPERTIES *pProperties);
    HRESULT FillBuffer(IMediaSample *pms);

    // Property support for resolution and frame rate
    HRESULT SetMediaType(const CMediaType *pmt);

private:
    enum Constants
    {
        DEFAULT_WIDTH = 1920,
        DEFAULT_HEIGHT = 1080,
        DEFAULT_FPS = 30,
        DEFAULT_BITCOUNT = 12, // NV12 = 1.5 bytes per pixel
    };

    VIDEOINFOHEADER m_videoInfo;
    LONGLONG m_rtLastSampleTime;
    DWORD m_dwFrameCount;

    // SharedMemoryBridge integration
    HANDLE m_hMapFile = NULL;
    BYTE *m_pMapView = NULL;
    CRITICAL_SECTION m_mmfMutex;

    BOOL TryReadFromMMF(BYTE *pData, long cbData, IMediaSample *pms);
    void CleanupMemoryMappedFile();
};
