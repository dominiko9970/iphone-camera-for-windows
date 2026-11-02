#pragma once
class UCamCapturePin;
extern const AMOVIESETUP_FILTER sudFilterReg;

// {745E0377-3903-48E6-8AD0-C190A3F298AF}
DEFINE_GUID(CLSID_UCamCaptureFilter,
            0x745E0377, 0x3903, 0x48E6, 0x8A, 0xD0, 0xC1, 0x90, 0xA3, 0xF2, 0x98, 0xAF);

class UCamCaptureFilter : public CSource
{
public:
    DECLARE_IUNKNOWN;

    static CUnknown *WINAPI CreateInstance(IUnknown *punk, HRESULT *phr);

    UCamCapturePin *m_paStreams[1];

private:
    UCamCaptureFilter(IUnknown *punk, HRESULT *phr);
    ~UCamCaptureFilter();
};
