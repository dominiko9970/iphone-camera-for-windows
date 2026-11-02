#pragma once

class UCamCapturePin : public CSourceStream, public IAMStreamConfig, public IKsPropertySet
{
public:
    DECLARE_IUNKNOWN;
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** mt) override;
    STDMETHODIMP GetNumberOfCapabilities(int* count, int* size) override;
    STDMETHODIMP GetStreamCaps(int index, AM_MEDIA_TYPE** mt, BYTE* caps) override;
    STDMETHODIMP Set(REFGUID, DWORD, LPVOID, DWORD, LPVOID, DWORD) override { return E_NOTIMPL; }
    STDMETHODIMP Get(REFGUID set, DWORD id, LPVOID, DWORD, LPVOID data, DWORD size, DWORD* returned) override;
    STDMETHODIMP QuerySupported(REFGUID set, DWORD id, DWORD* support) override;
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
