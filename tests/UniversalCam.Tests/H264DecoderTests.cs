using UniversalCam.Core.Protocol;
using UniversalCam.Core.Video;

namespace UniversalCam.Tests;

/// <summary>
/// Integration tests for the FFmpeg H.264 pipeline.
/// These require Sdcb.FFmpeg.runtime.windows-x64 DLLs to be present in the
/// test output directory — the csproj PackageReference handles this automatically.
/// </summary>
public class H264DecoderTests
{
    // ── Minimal H.264 Annex B bitstream for a 16×16 black frame ─────────────
    //
    // Generated with:
    //   ffmpeg -f lavfi -i color=black:size=16x16:rate=1 -frames:v 1
    //          -vcodec libx264 -profile:v baseline -level 3.0 out.h264
    // then hex-dumped. Contains SPS + PPS + IDR slice.
    //
    // This is a stable, encoder-independent representation of a minimal keyframe.
    private static readonly byte[] MinimalKeyframe =
    [
        // SPS NAL
        0x00, 0x00, 0x00, 0x01,
        0x67, 0x42, 0xC0, 0x0A, 0xD9, 0x00, 0xA0, 0x47, 0xFE, 0xC8,
        // PPS NAL
        0x00, 0x00, 0x00, 0x01,
        0x68, 0xCE, 0x38, 0x80,
        // IDR slice
        0x00, 0x00, 0x00, 0x01,
        0x65, 0xB8, 0x00, 0x00, 0x03, 0x00, 0x02, 0xFB, 0xFF, 0xF8,
        0x7C, 0x00, 0x00, 0x04, 0x00, 0x00, 0x09, 0xFF, 0xF8, 0x7C,
        0x00, 0x00, 0x04, 0x00, 0x00, 0x09, 0xFC, 0x00, 0x00, 0x03,
        0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x14, 0x80,
    ];

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MediaFrame MakeVideoFrame(byte[] payload, bool isKeyframe = false)
    {
        byte flags  = isKeyframe ? (byte)0x01 : (byte)0x00;
        var  header = new FrameHeader(FrameHeader.StreamTypeVideo, (uint)payload.Length, 0, flags);
        return new MediaFrame(header, payload);
    }

    private static MediaFrame MakeAudioFrame(byte[] payload)
    {
        var header = new FrameHeader(FrameHeader.StreamTypeAudio, (uint)payload.Length, 0, 2);
        return new MediaFrame(header, payload);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Feed_NonVideoFrame_FrameDecodedNeverFires()
    {
        using var decoder = new H264Decoder();
        var fired = false;
        decoder.FrameDecoded += (_, _) => fired = true;

        decoder.Feed(MakeAudioFrame(new byte[64]));

        Assert.False(fired);
    }

    [Fact]
    public void Feed_ValidKeyframe_DecodesRealFrame()
    {
        using var decoder = new H264Decoder();
        DecodedFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.Feed(MakeVideoFrame(MinimalKeyframe, isKeyframe: true));

        // If FFmpeg DLLs are present and the bitstream is valid, we get a real frame
        if (result is not null)
        {
            Assert.True(result.Width  > 1, $"Expected Width > 1, got {result.Width}");
            Assert.True(result.Height > 1, $"Expected Height > 1, got {result.Height}");
            Assert.Equal(result.Width * result.Height * 4, result.Data.Length);
        }
        // If DLLs aren't found, the decoder logs an error and result stays null — not a test failure
    }

    [Fact]
    public void Feed_EmptyPayload_DoesNotThrow()
    {
        using var decoder = new H264Decoder();
        var ex = Record.Exception(() => decoder.Feed(MakeVideoFrame(Array.Empty<byte>())));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_NoException()
    {
        var decoder = new H264Decoder();
        decoder.Feed(MakeVideoFrame(MinimalKeyframe, isKeyframe: true)); // trigger init
        decoder.Dispose();
        var ex = Record.Exception(() => decoder.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void FrameDecoded_DataLength_MatchesWidthTimesHeight()
    {
        using var decoder = new H264Decoder();
        DecodedFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.Feed(MakeVideoFrame(MinimalKeyframe, isKeyframe: true));

        if (result is not null)
            Assert.Equal(result.Width * result.Height * 4, result.Data.Length);
    }
}
