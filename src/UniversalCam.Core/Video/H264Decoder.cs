using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Raw;
using UniversalCam.Core;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Video;

/// <summary>
/// Decodes H.264 Annex B NAL units (received from the iPhone) into BGRA32 frames
/// using raw FFmpeg P/Invoke — single-threaded, no managed callbacks.
///
/// Thread safety: Feed() must be called from a single thread.
/// FrameDecoded events fire synchronously on that thread.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    public event EventHandler<DecodedFrame>? FrameDecoded;

    private RawFfmpegDecoder? _decoder;
    private bool              _initialized;

    // Set from any thread; applied in Feed() before the next decode (same decoder thread).
    private int _resetPending;

    public void Feed(MediaFrame frame)
    {
        if (!frame.IsVideo) return;

        if (!_initialized)
        {
            try
            {
                _decoder = new RawFfmpegDecoder();
                _decoder.FrameDecoded += (_, f) => FrameDecoded?.Invoke(this, f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[H264Decoder] Init failed: {ex.Message}");
            }
            _initialized = true;
        }

        if (System.Threading.Interlocked.Exchange(ref _resetPending, 0) != 0)
            _decoder?.Reset();

        _decoder?.Decode(frame.Payload, frame.Header.PtsUs, frame.Header.IsKeyframe);
    }

    /// <summary>Thread-safe: schedules a flush before the next frame decode.</summary>
    public void RequestReset() => System.Threading.Interlocked.Exchange(ref _resetPending, 1);

    public void Reset() => _decoder?.Reset();

    public void Dispose()
    {
        _decoder?.Dispose();
        _decoder = null;
    }
}

/// <summary>A decoded video frame in BGRA32 format, ready for WPF WriteableBitmap rendering.</summary>
public sealed class DecodedFrame
{
    public int    Width  { get; }
    public int    Height { get; }
    public long   PtsUs  { get; }
    public byte[] Data   { get; }

    public DecodedFrame(int width, int height, long ptsUs, byte[] data)
    {
        Width  = width;
        Height = height;
        PtsUs  = ptsUs;
        Data   = data;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Raw FFmpeg P/Invoke — bypasses all Sdcb.FFmpeg managed wrappers so no
// managed delegate can be installed on native FFmpeg threads.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed unsafe class RawFfmpegDecoder : IDisposable
{
    public event EventHandler<DecodedFrame>? FrameDecoded;

    private AVCodecContext* _ctx;
    private AVFrame*        _frame;
    private bool            _waitingForKeyframe = true;
    private int             _consecutiveErrors;

    public RawFfmpegDecoder()
    {
        // Silence FFmpeg log output — AV_LOG_QUIET = -8
        // This also ensures no log callback ever fires on a native thread.
        NativeFFmpeg.av_log_set_level(-8);

        AVCodec* codec = NativeFFmpeg.avcodec_find_decoder(AVCodecID.H264);
        if (codec == null) throw new Exception("H.264 codec not found");

        _ctx = NativeFFmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new Exception("avcodec_alloc_context3 failed");

        // Single-threaded decode: prevents FFmpeg from spawning native OS threads
        // that are not CLR threads. A native non-CLR thread entering managed code
        // (e.g. via a managed log callback) causes Fatal CLR error 0x80131506.
        _ctx->thread_count = 1;
        _ctx->thread_type  = 0; // FF_THREAD_FRAME | FF_THREAD_SLICE — disable both

        int hr = NativeFFmpeg.avcodec_open2(_ctx, codec, null);
        if (hr < 0) throw new Exception($"avcodec_open2 failed: {hr}");

        _frame = NativeFFmpeg.av_frame_alloc();
        if (_frame == null) throw new Exception("av_frame_alloc failed");
    }

    public void Reset()
    {
        _waitingForKeyframe = true;
        _consecutiveErrors  = 0;
        NativeFFmpeg.avcodec_flush_buffers(_ctx);
    }

    public void Decode(byte[] annexBNalUnits, long ptsUs, bool isKeyframe)
    {
        if (_waitingForKeyframe)
        {
            if (!isKeyframe) return;
            _waitingForKeyframe = false;
        }

        AVPacket* pkt = NativeFFmpeg.av_packet_alloc();
        if (pkt == null) return;
        try
        {
            if (NativeFFmpeg.av_new_packet(pkt, annexBNalUnits.Length) < 0) return;
            fixed (byte* src = annexBNalUnits)
                Buffer.MemoryCopy(src, pkt->data, annexBNalUnits.Length, annexBNalUnits.Length);
            pkt->pts = ptsUs;

            int sendResult = NativeFFmpeg.avcodec_send_packet(_ctx, pkt);
            if (sendResult < 0) return;

            while (true)
            {
                int recvResult = NativeFFmpeg.avcodec_receive_frame(_ctx, _frame);
                if (recvResult == -11 || recvResult == NativeFFmpeg.AVERROR_EOF) break; // EAGAIN or EOF
                if (recvResult < 0)
                {
                    if (++_consecutiveErrors >= 10)
                    {
                        NativeFFmpeg.avcodec_flush_buffers(_ctx);
                        _waitingForKeyframe = true;
                        _consecutiveErrors  = 0;
                    }
                    break;
                }
                _consecutiveErrors = 0;
                EmitFrame(ptsUs);
                NativeFFmpeg.av_frame_unref(_frame);
            }
        }
        finally
        {
            NativeFFmpeg.av_packet_free(&pkt);
        }
    }

    private void EmitFrame(long ptsUs)
    {
        int w = _frame->width;
        int h = _frame->height;
        if (w <= 0 || h <= 0) return;
        if ((byte*)_frame->data[0] == null) return;
        if (_frame->linesize[0] <= 0) return;

        // Pure C# YUV→BGRA — no sws_scale, no native crash possible.
        // FFmpeg's H.264 software decoder always outputs yuv420p (fmt=0)
        // or yuvj420p (fmt=12); both use the same planar layout.
        byte[] bgra = new byte[w * h * 4];
        Yuv420pToBgra(
            (byte*)_frame->data[0], _frame->linesize[0],
            (byte*)_frame->data[1], _frame->linesize[1],
            (byte*)_frame->data[2], _frame->linesize[2],
            bgra, w, h);

        // Emit frame as-is (w×h). Portrait frames (h > w) are cropped/panned
        // by the consumer (MainWindow) so the user can choose which part to show.
        FrameDecoded?.Invoke(this, new DecodedFrame(w, h, ptsUs, bgra));
    }

    private static unsafe void Yuv420pToBgra(
        byte* yPlane,  int yStride,
        byte* uPlane,  int uStride,
        byte* vPlane,  int vStride,
        byte[] dst, int w, int h)
    {
        // BT.601 limited-range integer coefficients (same as sws_scale default for SD/HD)
        // R = clamp((298*(Y-16)           + 409*(V-128) + 128) >> 8)
        // G = clamp((298*(Y-16) - 100*(U-128) - 208*(V-128) + 128) >> 8)
        // B = clamp((298*(Y-16) + 516*(U-128)           + 128) >> 8)
        fixed (byte* dstPtr = dst)
        {
            for (int row = 0; row < h; row++)
            {
                byte* yRow = yPlane + row * yStride;
                byte* uRow = uPlane + (row >> 1) * uStride;
                byte* vRow = vPlane + (row >> 1) * vStride;
                byte* d    = dstPtr + row * w * 4;

                for (int col = 0; col < w; col++)
                {
                    int c = 298 * (yRow[col] - 16) + 128;
                    int u = uRow[col >> 1] - 128;
                    int v = vRow[col >> 1] - 128;

                    int r = (c          + 409 * v) >> 8;
                    int g = (c - 100 * u - 208 * v) >> 8;
                    int b = (c + 516 * u          ) >> 8;

                    d[col * 4 + 0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    d[col * 4 + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    d[col * 4 + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    d[col * 4 + 3] = 255;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; _frame = null; NativeFFmpeg.av_frame_free(&f); }
        if (_ctx   != null) { var c = _ctx;   _ctx   = null; NativeFFmpeg.avcodec_free_context(&c); }
    }
}

