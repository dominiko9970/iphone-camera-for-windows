using System;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// COM-visible IMFMediaStream implementation for the Windows 11 22H2+ virtual camera.
/// Windows calls RequestSample() each time a consumer (browser, Teams, OBS) needs a frame.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class MfMediaStream : IMFMediaStream, IDisposable
{
    private readonly MfMediaSourceWrapper _mediaSource;
    private readonly IMFStreamDescriptor  _streamDescriptor;
    private MfEventQueue? _eventQueue;
    private bool _isActive;
    private bool _isShutdown;
    private bool _disposed;

    public MfMediaStream(int streamId, MfMediaSourceWrapper mediaSource, IMFStreamDescriptor streamDescriptor)
    {
        _mediaSource      = mediaSource      ?? throw new ArgumentNullException(nameof(mediaSource));
        _streamDescriptor = streamDescriptor ?? throw new ArgumentNullException(nameof(streamDescriptor));

        int hr = NativeMF.MFCreateEventQueue(out _eventQueue);
        NativeMF.ThrowIfFailed(hr, "MFCreateEventQueue (stream)");
    }

    // ── Lifecycle helpers called by MfMediaSourceWrapper ──────────────────

    public void Start()
    {
        _isActive = true;
        Console.WriteLine("[MfMediaStream] Started");
    }

    public void Pause()
    {
        _isActive = false;
        Console.WriteLine("[MfMediaStream] Paused");
    }

    public void Stop()
    {
        _isActive = false;
        Console.WriteLine("[MfMediaStream] Stopped");
    }

    public void Shutdown()
    {
        if (_isShutdown) return;
        _isShutdown = true;
        _eventQueue?.Shutdown();
        Console.WriteLine("[MfMediaStream] Shutdown");
    }

    /// <summary>
    /// Signals MEStreamFormatChanged so consumers re-negotiate the media type.
    /// </summary>
    public void NotifyFormatChanged(int width, int height, long fps)
    {
        if (_isShutdown || _eventQueue == null) return;

        // Build a new media type for the updated resolution
        int hr = NativeMF.MFCreateMediaType(out var mediaType);
        if (!NativeMF.Succeeded(hr)) return;

        // Copy static GUIDs to locals — static readonly fields can't be passed as ref
        Guid keyMajorType = NativeMF.MF_MT_MAJOR_TYPE;
        Guid keySubtype   = NativeMF.MF_MT_SUBTYPE;
        Guid keyFrameSize = NativeMF.MF_MT_FRAME_SIZE;
        Guid keyFrameRate = NativeMF.MF_MT_FRAME_RATE;
        Guid valVideo     = NativeMF.MFMediaType_Video;
        Guid valNV12      = NativeMF.MFVideoFormat_NV12;
        Guid nullGuid     = NativeMF.GUID_NULL;

        mediaType.SetGUID(ref keyMajorType, ref valVideo);
        mediaType.SetGUID(ref keySubtype,   ref valNV12);
        ulong frameSize = ((ulong)width << 32) | (uint)height;
        mediaType.SetUINT64(ref keyFrameSize, frameSize);
        ulong frameRate = ((ulong)fps << 32) | 1;
        mediaType.SetUINT64(ref keyFrameRate, frameRate);

        _eventQueue.QueueEventParamUnk(NativeMF.MEStreamFormatChanged, ref nullGuid, 0, mediaType);
        Console.WriteLine($"[MfMediaStream] Format changed: {width}×{height} @ {fps}fps");
    }

    // ── IMFMediaEventGenerator (and the re-declared new slots on IMFMediaStream) ──

    void IMFMediaEventGenerator.GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
        => GetEventImpl(dwFlags, out ppEvent);
    void IMFMediaStream.GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
        => GetEventImpl(dwFlags, out ppEvent);
    private void GetEventImpl(uint dwFlags, out IMFMediaEvent ppEvent)
    {
        ThrowIfShutdown();
        _eventQueue!.GetEvent(dwFlags, out ppEvent);
    }

    void IMFMediaEventGenerator.BeginGetEvent(IMFAsyncCallback pCallback, object punkState)
        => BeginGetEventImpl(pCallback, punkState);
    void IMFMediaStream.BeginGetEvent(IMFAsyncCallback pCallback, object punkState)
        => BeginGetEventImpl(pCallback, punkState);
    private void BeginGetEventImpl(IMFAsyncCallback pCallback, object punkState)
    {
        ThrowIfShutdown();
        IntPtr cbPtr    = pCallback != null ? Marshal.GetIUnknownForObject(pCallback) : IntPtr.Zero;
        IntPtr statePtr = punkState != null ? Marshal.GetIUnknownForObject(punkState) : IntPtr.Zero;
        try { _eventQueue!.BeginGetEvent(cbPtr, statePtr); }
        finally
        {
            if (cbPtr    != IntPtr.Zero) Marshal.Release(cbPtr);
            if (statePtr != IntPtr.Zero) Marshal.Release(statePtr);
        }
    }

    void IMFMediaEventGenerator.EndGetEvent(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
        => EndGetEventImpl(pResult, out ppEvent);
    void IMFMediaStream.EndGetEvent(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
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
    void IMFMediaStream.QueueEvent(uint met, ref Guid guidExtendedType, int hrStatus, ref PropVariant pvValue)
        => QueueEventImpl(met, ref guidExtendedType, hrStatus, ref pvValue);
    private void QueueEventImpl(uint met, ref Guid guidExtendedType, int hrStatus, ref PropVariant pvValue)
    {
        ThrowIfShutdown();
        _eventQueue!.QueueEvent(met, ref guidExtendedType, hrStatus, ref pvValue);
    }

    // ── IMFMediaStream ─────────────────────────────────────────────────────

    void IMFMediaStream.GetMediaSource(out IMFMediaSource ppMediaSource)
    {
        ThrowIfShutdown();
        ppMediaSource = _mediaSource;
    }

    void IMFMediaStream.GetStreamDescriptor(out IMFStreamDescriptor ppStreamDescriptor)
    {
        ThrowIfShutdown();
        ppStreamDescriptor = _streamDescriptor;
    }

    void IMFMediaStream.RequestSample(object pToken)
    {
        ThrowIfShutdown();

        if (!_isActive)
            return;

        var frame = _mediaSource.GetNextFrame();
        if (frame == null)
        {
            // No frame available yet — queue an empty event so MF retries.
            // Returning silently here is fine; MF will call RequestSample again.
            return;
        }

        try
        {
            // Wrap the NV12 byte array in an IMFSample
            int dataLen = frame.Data.Length;

            int hr = NativeMF.MFCreateMemoryBuffer((uint)dataLen, out var buffer);
            NativeMF.ThrowIfFailed(hr, "MFCreateMemoryBuffer");

            buffer.Lock(out IntPtr pDst, out _, out _);
            Marshal.Copy(frame.Data, 0, pDst, dataLen);
            buffer.Unlock();
            buffer.SetCurrentLength((uint)dataLen);

            hr = NativeMF.MFCreateSample(out var sample);
            NativeMF.ThrowIfFailed(hr, "MFCreateSample");

            sample.AddBuffer(buffer);
            sample.SetSampleTime(frame.PtsUs * 10);       // µs → 100 ns units
            sample.SetSampleDuration(333_333);             // ~30 fps

            // If MF passed a token (for pull-mode), attach it to the sample
            if (pToken != null)
            {
                var tokenGuid = NativeMF.GUID_NULL;
                sample.SetUnknown(ref tokenGuid, pToken);
            }

            // Queue MEMediaSample so MF picks it up
            var nullGuid = NativeMF.GUID_NULL;
            _eventQueue!.QueueEventParamUnk(NativeMF.MEMediaSample, ref nullGuid, 0, sample);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MfMediaStream] RequestSample error: {ex.Message}");
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private void ThrowIfShutdown()
    {
        if (_isShutdown)
            throw new COMException("[MfMediaStream] Stream is shut down", NativeMF.MF_E_SHUTDOWN);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _eventQueue?.Dispose();
        _eventQueue = null;
    }
}
