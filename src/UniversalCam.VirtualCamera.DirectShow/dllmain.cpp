#include "pch.h"
#include <initguid.h>
#include "UCamCaptureFilter.h"

// Forward declarations
extern "C" STDAPI DllRegisterServer();
extern "C" STDAPI DllUnregisterServer();

// {ABCDEF01-2345-6789-ABCD-EF0123456789}
DEFINE_GUID(LIBID_UniversalCamVCam,
            0xABCDEF01, 0x2345, 0x6789, 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89);

// Global filter data
CFactoryTemplate g_Templates[] =
    {
        {L"UniversalCam Virtual Camera",
         &CLSID_UCamCaptureFilter,
         UCamCaptureFilter::CreateInstance,
         NULL,
         &sudFilterReg}};

int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);

STDAPI DllRegisterServer()
{
    HRESULT hr = AMovieDllRegisterServer2(TRUE);
    if (FAILED(hr)) return hr;
    HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IFilterMapper2* mapper = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, reinterpret_cast<void**>(&mapper));
    if (SUCCEEDED(hr)) {
        REGPINTYPES type = { &MEDIATYPE_Video, &MEDIASUBTYPE_NV12 };
        REGFILTERPINS2 pin = {};
        pin.dwFlags = REG_PINFLAG_B_OUTPUT;
        pin.cInstances = 1;
        pin.nMediaTypes = 1;
        pin.lpMediaType = &type;
        REGFILTER2 filter = {};
        filter.dwVersion = 2;
        filter.dwMerit = MERIT_DO_NOT_USE;
        filter.cPins2 = 1;
        filter.rgPins2 = &pin;
        hr = mapper->RegisterFilter(CLSID_UCamCaptureFilter, L"UniversalCam Virtual Camera",
            nullptr, &CLSID_VideoInputDeviceCategory, nullptr, &filter);
        mapper->Release();
    }
    if (SUCCEEDED(init)) CoUninitialize();
    return hr;
}

STDAPI DllUnregisterServer()
{
    HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IFilterMapper2* mapper = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, reinterpret_cast<void**>(&mapper)))) {
        mapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory, nullptr, CLSID_UCamCaptureFilter);
        mapper->Release();
    }
    if (SUCCEEDED(init)) CoUninitialize();
    return AMovieDllRegisterServer2(FALSE);
}

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    extern BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);
    return DllEntryPoint(hModule, ul_reason_for_call, lpReserved);
}
