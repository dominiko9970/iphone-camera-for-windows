using System;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

// ──────────────────────────────────────────────────────────────────────────────
// Media Foundation COM interfaces — vtable layout must exactly match mfobjects.h
// Each interface starts with the IUnknown slot (QueryInterface/AddRef/Release)
// inherited from the base, then adds its own methods in declaration order.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Key-value store used as base for many MF objects.</summary>
[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    void GetItemType([In] ref Guid guidKey, out uint pType);
    void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    void GetUINT32([In] ref Guid guidKey, out uint punValue);
    void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    void GetDouble([In] ref Guid guidKey, out double pfValue);
    void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    void DeleteItem([In] ref Guid guidKey);
    void DeleteAllItems();
    void SetUINT32([In] ref Guid guidKey, uint unValue);
    void SetUINT64([In] ref Guid guidKey, ulong unValue);
    void SetDouble([In] ref Guid guidKey, double fValue);
    void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    void LockStore();
    void UnlockStore();
    void GetCount(out uint pcItems);
    void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
}

/// <summary>Describes format of a media stream (major type, subtype, frame size, etc.).</summary>
[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    // IMFAttributes inherited — all 30 methods come first in vtable
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    // IMFMediaType own methods
    void IsEqual([MarshalAs(UnmanagedType.Interface)] IMFMediaType pIMediaType, out uint pdwFlags);
    void GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
    void FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
}

/// <summary>Raw byte buffer for media data.</summary>
[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    void Unlock();
    void GetCurrentLength(out uint pcbCurrentLength);
    void SetCurrentLength(uint cbCurrentLength);
    void GetMaxLength(out uint pcbMaxLength);
}

/// <summary>Container for one or more IMFMediaBuffer objects with timing info.</summary>
[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    // IMFAttributes inherited
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    // IMFSample own methods
    void GetSampleFlags(out uint pdwSampleFlags);
    void SetSampleFlags(uint dwSampleFlags);
    void GetSampleTime(out long phnsSampleTime);
    void SetSampleTime(long hnsSampleTime);
    void GetSampleDuration(out long phnsSampleDuration);
    void SetSampleDuration(long hnsSampleDuration);
    void GetBufferCount(out uint pdwBufferCount);
    void GetBufferByIndex(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
    void ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
    void AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
    void RemoveBufferByIndex(uint dwIndex);
    void RemoveAllBuffers();
    void GetTotalLength(out uint pcbTotalLength);
    void CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
}

/// <summary>
/// Direct vtable wrapper for the native IMFMediaEventQueue COM object.
/// .NET's QI-based COM casting fails for this interface, so we call the vtable directly.
/// IMFMediaEventQueue vtable (after IUnknown slots 0-2):
///   3=GetEvent  4=BeginGetEvent  5=EndGetEvent
///   6=QueueEvent  7=QueueEventParamVar  8=QueueEventParamUnk  9=Shutdown
/// </summary>
internal sealed class MfEventQueue : IDisposable
{
    private IntPtr _ptr;

    public bool IsValid => _ptr != IntPtr.Zero;

    public MfEventQueue(IntPtr ptr) { _ptr = ptr; }

    // ── Vtable delegate types (x64 COM = Winapi) ──────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetEventFn(IntPtr self, uint dwFlags, out IntPtr ppEvent);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int BeginGetEventFn(IntPtr self, IntPtr pCallback, IntPtr punkState);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EndGetEventFn(IntPtr self, IntPtr pResult, out IntPtr ppEvent);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueueEventFn(IntPtr self, uint met, ref Guid ext, int hr, ref PropVariant pv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueueEventParamVarFn(IntPtr self, uint met, ref Guid ext, int hr, ref PropVariant pv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueueEventParamUnkFn(IntPtr self, uint met, ref Guid ext, int hr, IntPtr pUnk);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ShutdownFn(IntPtr self);

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Forwards GetEvent from IMFMediaEventGenerator implementation to native queue.</summary>
    public void GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
    {
        if (_ptr == IntPtr.Zero) throw new COMException("event queue disposed", NativeMF.MF_E_SHUTDOWN);
        int hr = GetVtable<GetEventFn>(3)(_ptr, dwFlags, out IntPtr evPtr);
        NativeMF.ThrowIfFailed(hr, "EventQueue.GetEvent");
        ppEvent = (IMFMediaEvent)Marshal.GetObjectForIUnknown(evPtr);
        Marshal.Release(evPtr);
    }

    /// <summary>Forwards BeginGetEvent — native MF uses this for async event delivery.</summary>
    public void BeginGetEvent(IntPtr pCallback, IntPtr punkState)
    {
        if (_ptr == IntPtr.Zero) return;
        GetVtable<BeginGetEventFn>(4)(_ptr, pCallback, punkState);
    }

    /// <summary>Forwards EndGetEvent.</summary>
    public void EndGetEvent(IntPtr pResult, out IMFMediaEvent ppEvent)
    {
        if (_ptr == IntPtr.Zero) throw new COMException("event queue disposed", NativeMF.MF_E_SHUTDOWN);
        int hr = GetVtable<EndGetEventFn>(5)(_ptr, pResult, out IntPtr evPtr);
        NativeMF.ThrowIfFailed(hr, "EventQueue.EndGetEvent");
        ppEvent = (IMFMediaEvent)Marshal.GetObjectForIUnknown(evPtr);
        Marshal.Release(evPtr);
    }

    public void QueueEvent(uint met, ref Guid ext, int hrStatus, ref PropVariant pv)
    {
        if (_ptr == IntPtr.Zero) return;
        GetVtable<QueueEventFn>(6)(_ptr, met, ref ext, hrStatus, ref pv);
    }

    public void QueueEventParamVar(uint met, ref Guid ext, int hrStatus, ref PropVariant pv)
    {
        if (_ptr == IntPtr.Zero) return;
        GetVtable<QueueEventParamVarFn>(7)(_ptr, met, ref ext, hrStatus, ref pv);
    }

    public void QueueEventParamUnk(uint met, ref Guid ext, int hrStatus, object? pUnk)
    {
        if (_ptr == IntPtr.Zero) return;
        IntPtr unkPtr = pUnk != null ? Marshal.GetIUnknownForObject(pUnk) : IntPtr.Zero;
        try
        {
            GetVtable<QueueEventParamUnkFn>(8)(_ptr, met, ref ext, hrStatus, unkPtr);
        }
        finally
        {
            if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
        }
    }

    public void Shutdown()
    {
        if (_ptr == IntPtr.Zero) return;
        GetVtable<ShutdownFn>(9)(_ptr);
    }

    private T GetVtable<T>(int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(_ptr);
        var fnPtr  = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    public void Dispose()
    {
        if (_ptr != IntPtr.Zero)
        {
            Marshal.Release(_ptr);
            _ptr = IntPtr.Zero;
        }
    }
}

/// <summary>Marker interface for MF event objects.</summary>
[ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent : IMFAttributes
{
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    void GetType(out uint pmet);
    void GetExtendedType(out Guid pguidExtendedType);
    void GetStatus(out int phrStatus);
    void GetValue([In, Out] ref PropVariant pvValue);
}

/// <summary>Async callback used by MF event infrastructure.</summary>
[ComImport, Guid("A27003CF-2354-4F2A-8D6A-AB7CFF15437E"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAsyncCallback
{
    void GetParameters(out uint pdwFlags, out uint pdwQueue);
    void Invoke([MarshalAs(UnmanagedType.Interface)] IMFAsyncResult pAsyncResult);
}

/// <summary>Carries the result of an async MF operation.</summary>
[ComImport, Guid("AC6B7889-0740-4D51-8619-905994A55CC6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAsyncResult
{
    void GetState([MarshalAs(UnmanagedType.IUnknown)] out object ppunkState);
    void GetStatus();
    void SetStatus(int hrStatus);
    void GetObject([MarshalAs(UnmanagedType.IUnknown)] out object ppObject);
    IntPtr GetStateNoAddRef();
}

/// <summary>Generates async MF events. Base for IMFMediaSource and IMFMediaStream.</summary>
[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    void GetEvent(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    void BeginGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncCallback pCallback, [MarshalAs(UnmanagedType.IUnknown)] object punkState);
    void EndGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncResult pResult, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    void QueueEvent(uint met, [In] ref Guid guidExtendedType, int hrStatus, [In] ref PropVariant pvValue);
}

/// <summary>Describes a single stream inside a presentation descriptor.</summary>
[ComImport, Guid("56C03D9C-9DBB-45F5-AB4B-D80F47C05938"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFStreamDescriptor : IMFAttributes
{
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    void GetStreamIdentifier(out uint pdwStreamIdentifier);
    void GetMediaTypeHandler([MarshalAs(UnmanagedType.Interface)] out IMFMediaTypeHandler ppMediaTypeHandler);
}

/// <summary>Gets/sets media types on a stream descriptor.</summary>
[ComImport, Guid("E93DCF6C-4B07-4E1E-8123-AA16ED6EADF5"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaTypeHandler
{
    void IsMediaTypeSupported([MarshalAs(UnmanagedType.Interface)] IMFMediaType pMediaType, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMediaType);
    void GetMediaTypeCount(out uint pdwTypeCount);
    void GetMediaTypeByIndex(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppType);
    void SetCurrentMediaType([MarshalAs(UnmanagedType.Interface)] IMFMediaType pMediaType);
    void GetCurrentMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMediaType);
    void GetMajorType(out Guid pguidMajorType);
}

/// <summary>Describes all streams in a presentation.</summary>
[ComImport, Guid("88DDCD21-03C3-4275-91ED-55EE3929328F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFPresentationDescriptor : IMFAttributes
{
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    void GetStreamDescriptorCount(out uint pdwDescriptorCount);
    void GetStreamDescriptorByIndex(uint dwIndex, out bool pfSelected, [MarshalAs(UnmanagedType.Interface)] out IMFStreamDescriptor ppDescriptor);
    void SelectStream(uint dwDescriptorIndex);
    void DeselectStream(uint dwDescriptorIndex);
    void Clone([MarshalAs(UnmanagedType.Interface)] out IMFPresentationDescriptor ppPresentationDescriptor);
}

/// <summary>Represents a media source (e.g., our virtual camera feed).</summary>
[ComImport, Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource : IMFMediaEventGenerator
{
    // IMFMediaEventGenerator inherited
    new void GetEvent(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    new void BeginGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncCallback pCallback, [MarshalAs(UnmanagedType.IUnknown)] object punkState);
    new void EndGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncResult pResult, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    new void QueueEvent(uint met, [In] ref Guid guidExtendedType, int hrStatus, [In] ref PropVariant pvValue);
    // IMFMediaSource own methods
    void GetCharacteristics(out uint pdwCharacteristics);
    void CreatePresentationDescriptor([MarshalAs(UnmanagedType.Interface)] out IMFPresentationDescriptor ppPresentationDescriptor);
    void Start([MarshalAs(UnmanagedType.Interface)] IMFPresentationDescriptor pPresentationDescriptor,
               [In] ref Guid pguidTimeFormat, [In] ref PropVariant pvarStartPosition);
    void Stop();
    void Pause();
    void Shutdown();
}

/// <summary>Represents a single video/audio stream within a media source.</summary>
[ComImport, Guid("D182108F-4EC6-443F-AA42-A71106EC825F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaStream : IMFMediaEventGenerator
{
    // IMFMediaEventGenerator inherited
    new void GetEvent(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    new void BeginGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncCallback pCallback, [MarshalAs(UnmanagedType.IUnknown)] object punkState);
    new void EndGetEvent([MarshalAs(UnmanagedType.Interface)] IMFAsyncResult pResult, [MarshalAs(UnmanagedType.Interface)] out IMFMediaEvent ppEvent);
    new void QueueEvent(uint met, [In] ref Guid guidExtendedType, int hrStatus, [In] ref PropVariant pvValue);
    // IMFMediaStream own methods
    void GetMediaSource([MarshalAs(UnmanagedType.Interface)] out IMFMediaSource ppMediaSource);
    void GetStreamDescriptor([MarshalAs(UnmanagedType.Interface)] out IMFStreamDescriptor ppStreamDescriptor);
    void RequestSample([MarshalAs(UnmanagedType.IUnknown)] object pToken);
}

/// <summary>Windows 11 22H2+ virtual camera handle — returned by MFCreateVirtualCamera.</summary>
[ComImport, Guid("1E8DDA8C-AE80-4E2A-9B5A-4E7E2F21EFC7"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFVirtualCamera : IMFAttributes
{
    new void GetItem([In] ref Guid guidKey, [In, Out] ref PropVariant pValue);
    new void GetItemType([In] ref Guid guidKey, out uint pType);
    new void CompareItem([In] ref Guid guidKey, [In] ref PropVariant Value, out bool pbResult);
    new void Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, uint MatchType, out bool pbResult);
    new void GetUINT32([In] ref Guid guidKey, out uint punValue);
    new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
    new void GetDouble([In] ref Guid guidKey, out double pfValue);
    new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
    new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
    new void GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszValue, uint cchBufSize, out uint pcchLength);
    new void GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
    new void GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
    new void SetItem([In] ref Guid guidKey, [In] ref PropVariant Value);
    new void DeleteItem([In] ref Guid guidKey);
    new void DeleteAllItems();
    new void SetUINT32([In] ref Guid guidKey, uint unValue);
    new void SetUINT64([In] ref Guid guidKey, ulong unValue);
    new void SetDouble([In] ref Guid guidKey, double fValue);
    new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    new void SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    new void SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out uint pcItems);
    new void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
    new void CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    // IMFVirtualCamera own methods
    void Start([MarshalAs(UnmanagedType.Interface)] IMFMediaSource pMediaSource);
    void Stop();
    void Remove();
}

// ──────────────────────────────────────────────────────────────────────────────
// P/Invoke declarations
// ──────────────────────────────────────────────────────────────────────────────

internal static class NativeMF
{
    // MF_VERSION for Windows 10+ (0x00020070 = MF SDK version 2.70)
    public const uint MF_VERSION = 0x00020070;
    // MFSTARTUP_LITE = no sockets (lighter init, fine for virtual camera)
    public const uint MFSTARTUP_LITE = 0x00000001;

    // ── Factory functions (mfplat.dll) ─────────────────────────────────────
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint Version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    // Returns raw IntPtr to avoid .NET QI issues with IMFMediaEventQueue.
    // Use MfEventQueue wrapper to call methods via direct vtable access.
    [DllImport("mfplat.dll", ExactSpelling = true, EntryPoint = "MFCreateEventQueue")]
    private static extern int MFCreateEventQueue_Native(out IntPtr ppMediaEventQueue);

    public static int MFCreateEventQueue(out MfEventQueue? queue)
    {
        int hr = MFCreateEventQueue_Native(out IntPtr ptr);
        queue = (Succeeded(hr) && ptr != IntPtr.Zero) ? new MfEventQueue(ptr) : null;
        return hr;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(
        [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(
        [MarshalAs(UnmanagedType.Interface)] out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(
        uint cbMaxLength,
        [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateStreamDescriptor(
        uint dwStreamIdentifier,
        uint cMediaTypes,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface)] IMFMediaType[] apMediaTypes,
        [MarshalAs(UnmanagedType.Interface)] out IMFStreamDescriptor ppDescriptor);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreatePresentationDescriptor(
        uint cStreamDescriptors,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface)] IMFStreamDescriptor[] apStreamDescriptors,
        [MarshalAs(UnmanagedType.Interface)] out IMFPresentationDescriptor ppPresentationDescriptor);

    // ── Virtual camera (mfvirtualcamera.dll, Windows 11 22H2+) ────────────
    [DllImport("mfvirtualcamera.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateVirtualCamera(
        int type,
        int lifetime,
        int access,
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        [MarshalAs(UnmanagedType.Interface)] IMFMediaSource pMediaSource,
        IntPtr pCategories,
        [MarshalAs(UnmanagedType.Interface)] out IMFVirtualCamera ppVirtualCamera);

    // ── Well-known GUIDs ───────────────────────────────────────────────────
    public static readonly Guid MFMediaType_Video         = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12        = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MF_MT_MAJOR_TYPE          = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE             = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE          = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE          = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO  = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE      = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid GUID_NULL                 = Guid.Empty;

    // ── MF event type constants (MediaEventType enum values) ──────────────
    public const uint MESourceStarted           = 6;
    public const uint MESourceStopped           = 7;
    public const uint MESourcePaused            = 9;
    public const uint MENewStream               = 10;
    public const uint MEUpdatedStream           = 11;
    public const uint MEMediaSample             = 42;
    public const uint MEStreamFormatChanged     = 108;
    public const uint MEEndOfStream             = 13;

    // ── HRESULT helpers ────────────────────────────────────────────────────
    public static bool Succeeded(int hr) => hr >= 0;
    public static void ThrowIfFailed(int hr, string context)
    {
        if (hr < 0)
            throw new COMException($"MF call failed in {context}", hr);
    }

    // MFVirtualCamera type/lifetime/access constants
    public const int MFVirtualCamera_Software   = 0;
    public const int MFVirtualCamera_Session    = 0; // lifetime: per-session
    public const int MFVirtualCamera_AllUsers   = 0; // access: current user

    // MFMediaSource characteristic flags
    public const uint MFMEDIASOURCE_IS_LIVE     = 0x4;

    // MF_E_SHUTDOWN (returned by event queue after Shutdown() called)
    public const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85);
}

// ──────────────────────────────────────────────────────────────────────────────
// PROPVARIANT — minimal struct sufficient for firing MF events with VT_EMPTY
// ──────────────────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;        // VARTYPE
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public long   data;      // covers the union for common types

    public static PropVariant Empty => new PropVariant { vt = 0 }; // VT_EMPTY
}
