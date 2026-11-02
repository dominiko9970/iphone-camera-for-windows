using Sdcb.FFmpeg.Raw;
using UniversalCam.Core;

namespace UniversalCam.Core.Audio;

/// <summary>
/// Decodes raw AAC-LC frames (received from the iPhone) to S16 interleaved PCM
/// using raw FFmpeg P/Invoke — single-threaded, no managed callbacks.
/// </summary>
public sealed class AacDecoder : IDisposable
{
    public event EventHandler<PcmFrame>? PcmDecoded;

    private unsafe AVCodecContext* _ctx;
    private unsafe AVFrame*        _frame;
    private bool _disposed;

    public unsafe AacDecoder()
    {
        NativeFFmpeg.av_log_set_level(-8); // AV_LOG_QUIET — no native log callbacks

        AVCodec* codec = NativeFFmpeg.avcodec_find_decoder(AVCodecID.Aac);
        if (codec == null) throw new Exception("AAC codec not found");

        _ctx = NativeFFmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new Exception("avcodec_alloc_context3 failed");

        _ctx->thread_count = 1;
        _ctx->thread_type  = 0;

        int hr = NativeFFmpeg.avcodec_open2(_ctx, codec, null);
        if (hr < 0) throw new Exception($"avcodec_open2 failed: {hr}");

        _frame = NativeFFmpeg.av_frame_alloc();
        if (_frame == null) throw new Exception("av_frame_alloc failed");
    }

    public unsafe void Decode(byte[] aacData, long ptsUs)
    {
        if (_disposed) return;

        AVPacket* pkt = NativeFFmpeg.av_packet_alloc();
        if (pkt == null) return;
        try
        {
            if (NativeFFmpeg.av_new_packet(pkt, aacData.Length) < 0) return;
            fixed (byte* src = aacData)
                Buffer.MemoryCopy(src, pkt->data, aacData.Length, aacData.Length);
            pkt->pts = ptsUs;

            if (NativeFFmpeg.avcodec_send_packet(_ctx, pkt) < 0) return;

            while (true)
            {
                int result = NativeFFmpeg.avcodec_receive_frame(_ctx, _frame);
                if (result == NativeFFmpeg.AVERROR_EAGAIN || result == NativeFFmpeg.AVERROR_EOF) break;
                if (result < 0) break;

                EmitPcm(ptsUs);
                NativeFFmpeg.av_frame_unref(_frame);
            }
        }
        finally
        {
            NativeFFmpeg.av_packet_free(&pkt);
        }
    }

    private unsafe void EmitPcm(long ptsUs)
    {
        int sampleRate = _frame->sample_rate;
        int nbSamples  = _frame->nb_samples;
        if (sampleRate <= 0 || nbSamples <= 0) return;
        if ((byte*)_frame->data[0] == null) return;

        // FLTP: each channel in its own plane as float[nbSamples]
        float* left  = (float*)_frame->data[0];
        float* right = _frame->data[1] != 0 ? (float*)_frame->data[1] : left; // mono fallback

        byte[] output = new byte[nbSamples * 2 * 2];
        fixed (byte* dst = output)
        {
            short* s = (short*)dst;
            for (int i = 0; i < nbSamples; i++)
            {
                s[i * 2 + 0] = FloatToS16(left[i]);
                s[i * 2 + 1] = FloatToS16(right[i]);
            }
        }

        PcmDecoded?.Invoke(this, new PcmFrame(output, sampleRate, 2, ptsUs));
    }

    private static short FloatToS16(float f)
    {
        int v = (int)(f * 32767f);
        return (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_frame != null) { var f = _frame; _frame = null; NativeFFmpeg.av_frame_free(&f); }
        if (_ctx   != null) { var c = _ctx;   _ctx   = null; NativeFFmpeg.avcodec_free_context(&c); }
    }
}

/// <summary>S16 interleaved PCM audio ready for WASAPI playback.</summary>
public sealed class PcmFrame
{
    public byte[] Data     { get; }
    public int    Rate     { get; }
    public int    Channels { get; }
    public long   PtsUs    { get; }

    public PcmFrame(byte[] data, int rate, int channels, long ptsUs)
    {
        Data     = data;
        Rate     = rate;
        Channels = channels;
        PtsUs    = ptsUs;
    }
}
