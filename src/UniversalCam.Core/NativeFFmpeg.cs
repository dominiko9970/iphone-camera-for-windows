using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Raw;

namespace UniversalCam.Core;

/// <summary>
/// Raw P/Invoke bindings for libavcodec, libavutil, and libswscale.
/// No managed wrappers, no callbacks — native threads stay native.
/// </summary>
internal static unsafe class NativeFFmpeg
{
    public const int AVERROR_EOF  = -541478725; // AVERROR(EOF)
    public const int AVERROR_EAGAIN = -11;       // EAGAIN on Linux; avcodec uses -11 for "send more input"

    // ── libavcodec-60 ─────────────────────────────────────────────────────────
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern AVCodec*        avcodec_find_decoder(AVCodecID id);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern AVCodecContext* avcodec_alloc_context3(AVCodec* codec);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern int             avcodec_open2(AVCodecContext* avctx, AVCodec* codec, AVDictionary** options);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern int             avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern int             avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern void            avcodec_flush_buffers(AVCodecContext* avctx);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern void            avcodec_free_context(AVCodecContext** avctx);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern AVPacket*       av_packet_alloc();
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern int             av_new_packet(AVPacket* pkt, int size);
    [DllImport("avcodec-60", CallingConvention = CallingConvention.Cdecl)] public static extern void            av_packet_free(AVPacket** pkt);

    // ── libavutil-58 ──────────────────────────────────────────────────────────
    [DllImport("avutil-58", CallingConvention = CallingConvention.Cdecl)] public static extern AVFrame* av_frame_alloc();
    [DllImport("avutil-58", CallingConvention = CallingConvention.Cdecl)] public static extern void     av_frame_unref(AVFrame* frame);
    [DllImport("avutil-58", CallingConvention = CallingConvention.Cdecl)] public static extern void     av_frame_free(AVFrame** frame);
    [DllImport("avutil-58", CallingConvention = CallingConvention.Cdecl)] public static extern void     av_log_set_level(int level);

    // ── libswscale-7 ──────────────────────────────────────────────────────────
    [DllImport("swscale-7", CallingConvention = CallingConvention.Cdecl)] public static extern SwsContext* sws_getContext(int srcW, int srcH, AVPixelFormat srcFormat, int dstW, int dstH, AVPixelFormat dstFormat, int flags, void* srcFilter, void* dstFilter, double* param);
    [DllImport("swscale-7", CallingConvention = CallingConvention.Cdecl)] public static extern int         sws_scale(SwsContext* c, byte** srcSlice, int* srcStride, int srcSliceY, int srcSliceH, byte** dst, int* dstStride);
    [DllImport("swscale-7", CallingConvention = CallingConvention.Cdecl)] public static extern void        sws_freeContext(SwsContext* swsContext);
}
