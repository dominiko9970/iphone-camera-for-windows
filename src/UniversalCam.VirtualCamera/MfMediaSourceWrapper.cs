using System;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// COM-visible IMFMediaSource implementation for the Windows 11 22H2+ virtual camera.
/// Windows calls into this object when a browser/Teams/OBS opens the virtual camera.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class MfMediaSourceWrapper : IMFMediaSource, IDisposable
{
    private readonly FrameBuffer _frameBuffer;
    private MfEventQueue? _eventQueue;
    private MfMediaStream? _mediaStream;
    private IMFStreamDescriptor? _streamDescriptor;
    private IMFPresentationDescriptor? _presentationDescriptor;

    private int _width  = 1920;
    private int _height = 1080;
    private long _fps   = 30;
    private bool _isShutdown;
    private bool _disposed;

    public int  Width          => _width;
    public int  Height         => _height;
    public long FramesPerSecond => _fps;

    public MfMediaSourceWrapper(FrameBuffer frameBuffer)
    {
        _frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));

        int hr = NativeMF.MFCreateEventQueue(out _eventQueue);
        if (!NativeMF.Succeeded(hr))
            throw new COMException("[MfMediaSourceWrapper] MFCreateEventQueue failed", hr);

        try
        {
            // Build initial stream descriptor and media stream
            BuildStreamDescriptor(_width, _height, _fps);
            _mediaStream = new MfMediaStream(0, this, _streamDescriptor!);
        }
        catch
        {
            // Shut down and release the event queue before propagating so MF's native
            // async threads can't call back into this partially-constructed object → 0x80131506.
            try { _eventQueue?.Shutdown(); } catch { }
            try { _eventQueue?.Dispose(); }  catch { }
            _eventQueue = null;
            throw;
        }
    }

    // ── Public helpers called by MfVirtualCameraServer ────────────────────

    public void UpdateMediaType(int width, int height, long fps)
    {
        _width  = width;
        _height = height;
        _fps    = fps;
        Console.WriteLine($"[MfMediaSourceWrapper] Media type updated: {width}×{height} @ {fps}fps");
        _mediaStream?.NotifyFormatChanged(width, height, fps);
    }

    public NV12Frame? GetNextFrame() =>
        _frameBuffer.TryDequeue(out var frame) ? frame : null;

    public void ClearFrames() => _frameBuffer.Clear();

    public MfMediaStream? GetStreamByIndex(int index) =>
        index == 0 ? _mediaStream : null;

    // ── IMFMediaEventGenerator (and the re-declared new slots on IMFMediaSource) ──

    void IMFMediaEventGenerator.GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
        => GetEventImpl(dwFlags, out ppEvent);
    void IMFMediaSource.GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
        => GetEventImpl(dwFlags, out ppEvent);
    private void GetEventImpl(uint dwFlags, out IMFMediaEvent ppEvent)
    {
        ThrowIfShutdown();
        _eventQueue!.GetEvent(dwFlags, out ppEvent);
    }

    void IMFMediaEventGenerator.BeginGetEvent(IMFAsyncCallback pCallback, object punkState)
        => BeginGetEventImpl(pCallback, punkState);
    void IMFMediaSource.BeginGetEvent(IMFAsyncCallback pCallback, object punkState)
        => BeginGetEventImpl(pCallback, punkState);
    private void BeginGetEventImpl(IMFAsyncCallback pCallback, object punkState)
    {
        ThrowIfShutdown();
        // Convert managed COM objects to raw pointers for vtable call
        IntPtr cbPtr    = pCallback  != null ? Marshal.GetIUnknownForObject(pCallback)  : IntPtr.Zero;
        IntPtr statePtr = punkState  != null ? Marshal.GetIUnknownForObject(punkState)  : IntPtr.Zero;
        try { _eventQueue!.BeginGetEvent(cbPtr, statePtr); }
        finally
        {
            if (cbPtr    != IntPtr.Zero) Marshal.Release(cbPtr);
            if (statePtr != IntPtr.Zero) Marshal.Release(statePtr);
        }
    }

    void IMFMediaEventGenerator.EndGetEvent(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
        => EndGetEventImpl(pResult, out ppEvent);
    void IMFMediaSource.EndGetEvent(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
        => EndGetEventImpl(pResult, out ppEvent);
    private void EndGetEventImpl(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
    {
        ThrowIfShutdown();
        IntPtr resPtr = pResult != null ? Marshal.GetIUnknownForObject(pResult) : IntPtr.Zero;
        try { _eventQueue!.EndGetEvent(resPtr, out ppEvent); }
        finally { if (resPtr != IntPtr.Zero) Marshal.Release(resPtr); }
    }

    void IMFMediaEventGenerator.QueueEvent(uint met, ref Guid guidExtendedType, int hrStatus, ref PropVariant pvValue)
        => QueueEventImpl(met, ref guidExtendedType, hrStatus, ref pvValue);
    void IMFMediaSource.QueueEvent(uint met, ref Guid guidExtendedType, int hrStatus, ref PropVariant pvValue)
        => QueueEventImpl(met, ref guidExtendedType, hrStatus, ref pvValue);
    private void QueueEventImpl(uint met, ref Guid guidExtendedType, int hrStatus, ref PropVariant pvValue)
    {
        ThrowIfShutdown();
        _eventQueue!.QueueEvent(met, ref guidExtendedType, hrStatus, ref pvValue);
    }

    // ── IMFMediaSource ─────────────────────────────────────────────────────

    void IMFMediaSource.GetCharacteristics(out uint pdwCharacteristics)
    {
        ThrowIfShutdown();
        pdwCharacteristics = NativeMF.MFMEDIASOURCE_IS_LIVE;
    }

    void IMFMediaSource.CreatePresentationDescriptor(out IMFPresentationDescriptor ppPresentationDescriptor)
    {
        ThrowIfShutdown();
        if (_presentationDescriptor == null)
            throw new COMException("[MfMediaSourceWrapper] Presentation descriptor not built", unchecked((int)0x80070057));
        // Clone so callers can't mutate our copy
        _presentationDescriptor.Clone(out ppPresentationDescriptor);
    }

    void IMFMediaSource.Start(IMFPresentationDescriptor pPresentationDescriptor,
                              ref Guid pguidTimeFormat, ref PropVariant pvarStartPosition)
    {
        ThrowIfShutdown();
        _mediaStream?.Start();

        // Fire MENewStream so MF knows our stream exists
        var empty = PropVariant.Empty;
        var nullGuid = NativeMF.GUID_NULL;
        _eventQueue!.QueueEventParamUnk(NativeMF.MENewStream, ref nullGuid, 0, _mediaStream!);

        // Fire MESourceStarted
        _eventQueue.QueueEventParamVar(NativeMF.MESourceStarted, ref nullGuid, 0, ref empty);
        Console.WriteLine("[MfMediaSourceWrapper] Started");
    }

    void IMFMediaSource.Stop()
    {
        ThrowIfShutdown();
        _mediaStream?.Stop();
        var empty    = PropVariant.Empty;
        var nullGuid = NativeMF.GUID_NULL;
        _eventQueue!.QueueEventParamVar(NativeMF.MESourceStopped, ref nullGuid, 0, ref empty);
        Console.WriteLine("[MfMediaSourceWrapper] Stopped");
    }

    void IMFMediaSource.Pause()
    {
        ThrowIfShutdown();
        _mediaStream?.Pause();
        var empty    = PropVariant.Empty;
        var nullGuid = NativeMF.GUID_NULL;
        _eventQueue!.QueueEventParamVar(NativeMF.MESourcePaused, ref nullGuid, 0, ref empty);
        Console.WriteLine("[MfMediaSourceWrapper] Paused");
    }

    void IMFMediaSource.Shutdown()
    {
        if (_isShutdown) return;
        _isShutdown = true;
        _mediaStream?.Shutdown();
        _eventQueue?.Shutdown();
        Console.WriteLine("[MfMediaSourceWrapper] Shutdown");
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private void BuildStreamDescriptor(int width, int height, long fps)
    {
        // Create an NV12 media type
        int hr = NativeMF.MFCreateMediaType(out var mediaType);
        NativeMF.ThrowIfFailed(hr, "MFCreateMediaType");

        // Copy static GUIDs to locals — static readonly fields can't be passed as ref
        Guid keyMajorType  = NativeMF.MF_MT_MAJOR_TYPE;
        Guid keySubtype    = NativeMF.MF_MT_SUBTYPE;
        Guid keyFrameSize  = NativeMF.MF_MT_FRAME_SIZE;
        Guid keyFrameRate  = NativeMF.MF_MT_FRAME_RATE;
        Guid keyPar        = NativeMF.MF_MT_PIXEL_ASPECT_RATIO;
        Guid keyInterlace  = NativeMF.MF_MT_INTERLACE_MODE;
        Guid keyAllIndep   = NativeMF.MF_MT_ALL_SAMPLES_INDEPENDENT;
        Guid valVideo      = NativeMF.MFMediaType_Video;
        Guid valNV12       = NativeMF.MFVideoFormat_NV12;

        mediaType.SetGUID(ref keyMajorType, ref valVideo);
        mediaType.SetGUID(ref keySubtype,   ref valNV12);

        // Frame size packed as (width << 32 | height)
        ulong frameSize = ((ulong)width << 32) | (uint)height;
        mediaType.SetUINT64(ref keyFrameSize, frameSize);

        // Frame rate packed as (numerator << 32 | denominator)
        ulong frameRate = ((ulong)fps << 32) | 1;
        mediaType.SetUINT64(ref keyFrameRate, frameRate);

        // Pixel aspect ratio 1:1
        ulong par = (1UL << 32) | 1;
        mediaType.SetUINT64(ref keyPar, par);

        // Progressive frames
        mediaType.SetUINT32(ref keyInterlace, 2);   // MFVideoInterlace_Progressive
        mediaType.SetUINT32(ref keyAllIndep,  1);

        // Build stream descriptor from the media type
        hr = NativeMF.MFCreateStreamDescriptor(0, 1, new[] { mediaType }, out _streamDescriptor);
        NativeMF.ThrowIfFailed(hr, "MFCreateStreamDescriptor");

        // Set the current media type on the handler
        _streamDescriptor!.GetMediaTypeHandler(out var handler);
        handler.SetCurrentMediaType(mediaType);

        // Build presentation descriptor
        hr = NativeMF.MFCreatePresentationDescriptor(1, new[] { _streamDescriptor }, out _presentationDescriptor);
        NativeMF.ThrowIfFailed(hr, "MFCreatePresentationDescriptor");
        _presentationDescriptor!.SelectStream(0);
    }

    private void ThrowIfShutdown()
    {
        if (_isShutdown)
            throw new COMException("[MfMediaSourceWrapper] Source is shut down", NativeMF.MF_E_SHUTDOWN);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Shutdown BEFORE releasing — cancels any pending BeginGetEvent callbacks so
        // MF native threads stop calling into this managed COM object. Without this,
        // a queued MF event fires on an unregistered native thread → 0x80131506 CLR fatal.
        if (!_isShutdown)
        {
            _isShutdown = true;
            try { _mediaStream?.Shutdown(); } catch { }
            try { _eventQueue?.Shutdown(); }  catch { }
        }

        _mediaStream?.Dispose();
        _frameBuffer.Clear();
        if (_eventQueue != null)
        {
            try { Marshal.ReleaseComObject(_eventQueue); } catch { }
            _eventQueue = null;
        }
    }
}
