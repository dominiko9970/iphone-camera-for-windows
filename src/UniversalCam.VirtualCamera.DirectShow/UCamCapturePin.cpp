#include "pch.h"
#include "UCamCapturePin.h"

// Frame header structure matching C# SharedMemoryBridge
#pragma pack(push, 1)
struct FrameHeader
{
    DWORD width;
    DWORD height;
    DWORD stride;
    DWORD dataSize;
    LONGLONG sequenceNo;
    LONGLONG ptsUs;
};
#pragma pack(pop)
static_assert(sizeof(FrameHeader) == 32, "Frame header must match the C# shared-memory layout");

extern const AMOVIESETUP_MEDIATYPE sudOpPinTypes =
    {
        &MEDIATYPE_Video,  // GUID of the media type
        &MEDIASUBTYPE_NV12 // GUID of the media subtype
};

UCamCapturePin::UCamCapturePin(HRESULT *phr, CSource *pFilter)
    : CSourceStream(NAME("UCamCapturePin"), phr, pFilter, L"Capture"),
      m_rtLastSampleTime(0),
      m_dwFrameCount(0)
{
    // Initialize video info header
    ZeroMemory(&m_videoInfo, sizeof(m_videoInfo));

    VIDEOINFOHEADER &vih = m_videoInfo;
    vih.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih.bmiHeader.biWidth = DEFAULT_WIDTH;
    vih.bmiHeader.biHeight = DEFAULT_HEIGHT;
    vih.bmiHeader.biBitCount = DEFAULT_BITCOUNT;
    vih.bmiHeader.biCompression = mmioFOURCC('N', 'V', '1', '2');
    vih.bmiHeader.biSizeImage = DEFAULT_WIDTH * DEFAULT_HEIGHT * 3 / 2;
    vih.bmiHeader.biPlanes = 1;
    vih.bmiHeader.biXPelsPerMeter = 0;
    vih.bmiHeader.biYPelsPerMeter = 0;
    vih.bmiHeader.biClrUsed = 0;
    vih.bmiHeader.biClrImportant = 0;

    // Set frame rate: 30fps
    vih.AvgTimePerFrame = (LONGLONG)(10000000.0 / DEFAULT_FPS);

    InitializeCriticalSection(&m_mmfMutex);

    if (FAILED(*phr))
        return;
}

UCamCapturePin::~UCamCapturePin()
{
    CleanupMemoryMappedFile();
    DeleteCriticalSection(&m_mmfMutex);
}

HRESULT UCamCapturePin::CheckMediaType(const CMediaType *pMediaType)
{
    if (pMediaType == NULL)
        return E_POINTER;

    if (*pMediaType->Type() != MEDIATYPE_Video)
        return E_INVALIDARG;

    if (*pMediaType->Subtype() != MEDIASUBTYPE_NV12)
        return E_INVALIDARG;

    if (*pMediaType->FormatType() != FORMAT_VideoInfo)
        return E_INVALIDARG;

    return S_OK;
}

HRESULT UCamCapturePin::GetMediaType(int iPosition, CMediaType *pmt)
{
    if (iPosition < 0)
        return E_INVALIDARG;
    if (iPosition > 0)
        return VFW_S_NO_MORE_ITEMS;

    if (pmt == NULL)
        return E_POINTER;

    pmt->SetType(&MEDIATYPE_Video);
    pmt->SetSubtype(&MEDIASUBTYPE_NV12);
    pmt->SetFormatType(&FORMAT_VideoInfo);
    pmt->SetTemporalCompression(FALSE);

    VIDEOINFOHEADER *pVih = (VIDEOINFOHEADER *)pmt->AllocFormatBuffer(sizeof(VIDEOINFOHEADER));
    if (pVih == NULL)
        return E_OUTOFMEMORY;

    CopyMemory(pVih, &m_videoInfo, sizeof(VIDEOINFOHEADER));
    pmt->SetSampleSize(m_videoInfo.bmiHeader.biSizeImage);

    return S_OK;
}

HRESULT UCamCapturePin::DecideBufferSize(IMemAllocator *pAlloc, ALLOCATOR_PROPERTIES *pProperties)
{
    ASSERT(pAlloc);
    ASSERT(pProperties);

    pProperties->cBuffers = 1;
    pProperties->cbBuffer = m_videoInfo.bmiHeader.biSizeImage;

    ALLOCATOR_PROPERTIES Actual;
    HRESULT hr = pAlloc->SetProperties(pProperties, &Actual);
    if (FAILED(hr))
        return hr;

    if (Actual.cbBuffer < pProperties->cbBuffer)
        return E_FAIL;

    return S_OK;
}

HRESULT UCamCapturePin::SetMediaType(const CMediaType *pmt)
{
    HRESULT hr = CSourceStream::SetMediaType(pmt);
    if (FAILED(hr))
        return hr;

    VIDEOINFOHEADER *pVih = (VIDEOINFOHEADER *)pmt->Format();
    if (pVih == NULL)
        return E_POINTER;

    CopyMemory(&m_videoInfo, pVih, sizeof(VIDEOINFOHEADER));
    return S_OK;
}

HRESULT UCamCapturePin::FillBuffer(IMediaSample *pms)
{
    ASSERT(pms);

    BYTE *pData = NULL;
    long cbData = pms->GetSize();

    HRESULT hr = pms->GetPointer(&pData);
    if (FAILED(hr))
        return hr;

    // Pace this live source even when no phone is connected.
    Sleep(static_cast<DWORD>(m_videoInfo.AvgTimePerFrame / 10000));

    // Attempt to read from SharedMemoryBridge
    if (TryReadFromMMF(pData, cbData, pms))
    {
        return S_OK;
    }

    // Fallback: Fill with black (Y=0, U=128, V=128) if MMF unavailable
    ZeroMemory(pData, cbData);
    int ySize = m_videoInfo.bmiHeader.biWidth * m_videoInfo.bmiHeader.biHeight;
    FillMemory(pData + ySize, cbData - ySize, 0x80);

    pms->SetActualDataLength(cbData);

    // Set timestamp
    LONGLONG rtStart = m_rtLastSampleTime;
    LONGLONG rtStop = rtStart + m_videoInfo.AvgTimePerFrame;

    pms->SetTime(&rtStart, &rtStop);
    m_rtLastSampleTime = rtStop;
    m_dwFrameCount++;
    pms->SetSyncPoint(TRUE);

    return S_OK;
}

BOOL UCamCapturePin::TryReadFromMMF(BYTE *pData, long cbData, IMediaSample *pms)
{
    EnterCriticalSection(&m_mmfMutex);

    BOOL bSuccess = FALSE;
    try
    {
        // Lazy-initialize MMF mapping on first call
        if (m_hMapFile == NULL)
        {
            m_hMapFile = OpenFileMappingW(FILE_MAP_READ, FALSE, L"UniversalCam_VCam_Frame");
            if (m_hMapFile == NULL)
            {
                // MMF not yet created; C# app may not be running
                goto cleanup;
            }

            m_pMapView = (BYTE *)MapViewOfFile(m_hMapFile, FILE_MAP_READ, 0, 0, 0);
            if (m_pMapView == NULL)
            {
                OutputDebugStringW(L"[UCamCapturePin] MapViewOfFile failed\r\n");
                goto cleanup;
            }
        }

        // Read frame header
        FrameHeader *pHeader = (FrameHeader *)m_pMapView;

        // Validate header
        if (pHeader->width == 0 || pHeader->height == 0 || pHeader->dataSize == 0)
        {
            goto cleanup; // Invalid frame; use black placeholder
        }

        const unsigned sw = pHeader->width, sh = pHeader->height;
        const unsigned dw = m_videoInfo.bmiHeader.biWidth, dh = m_videoInfo.bmiHeader.biHeight;
        if (sw > 3840 || sh > 2160 || (sw & 1) || (sh & 1) ||
            pHeader->stride != sw || pHeader->dataSize != sw * sh * 3 / 2 ||
            cbData < static_cast<long>(dw * dh * 3 / 2)) goto cleanup;
        const BYTE* src = m_pMapView + 64;
        // Keep the negotiated output format fixed; scale phone frames to it.
        for (unsigned y = 0; y < dh; ++y)
            for (unsigned x = 0; x < dw; ++x)
                pData[y * dw + x] = src[(y * sh / dh) * sw + x * sw / dw];
        for (unsigned y = 0; y < dh / 2; ++y)
            for (unsigned x = 0; x < dw / 2; ++x) {
                unsigned from = sw * sh + (y * (sh / 2) / (dh / 2)) * sw + (x * (sw / 2) / (dw / 2)) * 2;
                unsigned to = dw * dh + y * dw + x * 2;
                pData[to] = src[from]; pData[to + 1] = src[from + 1];
            }
        pms->SetActualDataLength(dw * dh * 3 / 2);

        // Translate PTS: C# uses microseconds, DirectShow uses 100ns units
        LONGLONG rtStart = m_rtLastSampleTime;
        LONGLONG rtStop = rtStart + m_videoInfo.AvgTimePerFrame;

        pms->SetTime(&rtStart, &rtStop);
        m_rtLastSampleTime = rtStop;

        m_dwFrameCount++;
        pms->SetSyncPoint(TRUE);
        bSuccess = TRUE;
    }
    catch (...)
    {
        OutputDebugStringW(L"[UCamCapturePin] Exception in TryReadFromMMF\r\n");
    }

cleanup:
    LeaveCriticalSection(&m_mmfMutex);
    return bSuccess;
}

void UCamCapturePin::CleanupMemoryMappedFile()
{
    EnterCriticalSection(&m_mmfMutex);

    if (m_pMapView != NULL)
    {
        UnmapViewOfFile(m_pMapView);
        m_pMapView = NULL;
    }

    if (m_hMapFile != NULL)
    {
        CloseHandle(m_hMapFile);
        m_hMapFile = NULL;
    }

    LeaveCriticalSection(&m_mmfMutex);
}

STDMETHODIMP UCamCapturePin::NonDelegatingQueryInterface(REFIID riid, void** ppv) {
    if (riid == IID_IAMStreamConfig) return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    if (riid == IID_IKsPropertySet) return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
    return CSourceStream::NonDelegatingQueryInterface(riid, ppv);
}
STDMETHODIMP UCamCapturePin::GetFormat(AM_MEDIA_TYPE** mt) {
    if (!mt) return E_POINTER;
    CMediaType type; HRESULT hr = GetMediaType(0, &type);
    if (FAILED(hr)) return hr;
    *mt = CreateMediaType(&type); return *mt ? S_OK : E_OUTOFMEMORY;
}
STDMETHODIMP UCamCapturePin::SetFormat(AM_MEDIA_TYPE* mt) {
    if (!mt) return E_POINTER;
    CMediaType type(*mt); HRESULT hr = CheckMediaType(&type);
    if (FAILED(hr)) return hr;
    auto vih = reinterpret_cast<VIDEOINFOHEADER*>(type.Format());
    if (!vih || vih->bmiHeader.biWidth != 1920 || vih->bmiHeader.biHeight != 1080) return VFW_E_TYPE_NOT_ACCEPTED;
    return SetMediaType(&type);
}
STDMETHODIMP UCamCapturePin::GetNumberOfCapabilities(int* count, int* size) {
    if (!count || !size) return E_POINTER;
    *count = 1; *size = sizeof(VIDEO_STREAM_CONFIG_CAPS); return S_OK;
}
STDMETHODIMP UCamCapturePin::GetStreamCaps(int index, AM_MEDIA_TYPE** mt, BYTE* caps) {
    if (!mt || !caps) return E_POINTER;
    if (index != 0) return S_FALSE;
    HRESULT hr = GetFormat(mt); if (FAILED(hr)) return hr;
    auto c = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(caps);
    ZeroMemory(c, sizeof(*c)); c->guid = FORMAT_VideoInfo;
    c->InputSize = c->MinCroppingSize = c->MaxCroppingSize = c->MinOutputSize = c->MaxOutputSize = {1920,1080};
    c->OutputGranularityX = c->OutputGranularityY = 1;
    c->MinFrameInterval = c->MaxFrameInterval = 333333;
    c->MinBitsPerSecond = c->MaxBitsPerSecond = 1920 * 1080 * 12 * 30;
    return S_OK;
}
STDMETHODIMP UCamCapturePin::Get(REFGUID set, DWORD id, LPVOID, DWORD, LPVOID data, DWORD size, DWORD* returned) {
    if (set != AMPROPSETID_Pin || id != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    if (returned) *returned = sizeof(GUID);
    if (!data) return S_OK;
    if (size < sizeof(GUID)) return E_UNEXPECTED;
    *static_cast<GUID*>(data) = PIN_CATEGORY_CAPTURE; return S_OK;
}
STDMETHODIMP UCamCapturePin::QuerySupported(REFGUID set, DWORD id, DWORD* support) {
    if (!support) return E_POINTER;
    if (set != AMPROPSETID_Pin || id != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    *support = KSPROPERTY_SUPPORT_GET; return S_OK;
}
