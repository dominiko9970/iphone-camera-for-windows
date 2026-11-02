#include "pch.h"
#include "UCamCaptureFilter.h"
#include "UCamCapturePin.h"
extern const AMOVIESETUP_MEDIATYPE sudOpPinTypes;

const AMOVIESETUP_PIN sudPins[] =
    {
        {
            L"Capture",    // Pins string name
            FALSE,         // Is it rendered
            TRUE,          // Is it an output
            FALSE,         // Allowed none
            FALSE,         // Allowed many
            &CLSID_NULL,   // Connects to filter
            NULL,          // Connects to pin
            1,             // Number of types
            &sudOpPinTypes // The pin details
        }};

extern const AMOVIESETUP_FILTER sudFilterReg =
    {
        &CLSID_UCamCaptureFilter,       // Filter CLSID
        L"UniversalCam Virtual Camera", // Filter name
        MERIT_DO_NOT_USE,               // Its merit
        1,                              // Number of pins
        sudPins                         // Pin details
};

CUnknown *WINAPI UCamCaptureFilter::CreateInstance(IUnknown *punk, HRESULT *phr)
{
    ASSERT(phr);
    CUnknown *punk2 = new UCamCaptureFilter(punk, phr);
    if (punk2 == NULL)
        *phr = E_OUTOFMEMORY;
    return punk2;
}

UCamCaptureFilter::UCamCaptureFilter(IUnknown *punk, HRESULT *phr)
    : CSource(NAME("UCamCaptureFilter"), punk, CLSID_UCamCaptureFilter)
{
    m_paStreams[0] = new UCamCapturePin(phr, this);
    if (m_paStreams[0] == NULL)
    {
        if (phr)
            *phr = E_OUTOFMEMORY;
    }
}

UCamCaptureFilter::~UCamCaptureFilter()
{
    // CSource owns and deletes its registered streams.
}
