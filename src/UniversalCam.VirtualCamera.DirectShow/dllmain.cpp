#include "pch.h"
#include "UCamCaptureFilter.h"

// Forward declarations
extern "C" STDAPI DllRegisterServer();
extern "C" STDAPI DllUnregisterServer();

// {745E0377-3903-48E6-8AD0-C190A3F298AF}  -- same as UCamCaptureFilter.h
DEFINE_GUID(CLSID_UCamCaptureFilter,
            0x745E0377, 0x3903, 0x48E6, 0x8A, 0xD0, 0xC1, 0x90, 0xA3, 0xF2, 0x98, 0xAF);

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
    return AMovieDllRegisterServer2(TRUE);
}

STDAPI DllUnregisterServer()
{
    return AMovieDllRegisterServer2(FALSE);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}
