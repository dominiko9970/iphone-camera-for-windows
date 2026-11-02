using System;
using System.Collections.Generic;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Media Foundation media source implementation for virtual camera.
/// Implements IMFMediaSource to provide a stream of NV12 frames.
/// </summary>
internal sealed class UCamMediaSource
{
    private readonly FrameBuffer _frameBuffer;
    private int _width = 1920;
    private int _height = 1080;
    private long _fps = 30;

    public UCamMediaSource(FrameBuffer frameBuffer)
    {
        _frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));
    }

    /// <summary>
    /// Updates the expected frame resolution and frame rate.
    /// Called when the iPhone sends a configure message.
    /// </summary>
    public void UpdateMediaType(int width, int height, long fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        Console.WriteLine($"[UCamMediaSource] Media type updated: {width}×{height} @ {fps}fps");
    }

    /// <summary>
    /// Gets the next frame from the buffer, or returns null if unavailable.
    /// </summary>
    public NV12Frame? GetNextFrame()
    {
        if (_frameBuffer.TryDequeue(out var frame))
            return frame;
        return null;
    }

    public int Width => _width;
    public int Height => _height;
    public long FramesPerSecond => _fps;
}
