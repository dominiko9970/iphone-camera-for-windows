using System;
using System.Drawing;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Generates a branded placeholder frame shown when no iPhone is connected.
/// Uses GDI+ to render the wordmark + "Connect your iPhone" text at 1920×1080.
/// </summary>
public sealed class PlaceholderFrameGenerator : IDisposable
{
    private const int DefaultWidth = 1920;
    private const int DefaultHeight = 1080;

    private byte[]? _cachedNv12;
    private int _cachedWidth;
    private int _cachedHeight;

    /// <summary>
    /// Generates a placeholder frame (conversion happens once per unique resolution).
    /// </summary>
    public NV12Frame GenerateFrame(int width = DefaultWidth, int height = DefaultHeight)
    {
        // Return cached frame if resolution hasn't changed
        if (_cachedNv12 != null && _cachedWidth == width && _cachedHeight == height)
        {
            return new NV12Frame
            {
                Width = width,
                Height = height,
                PtsUs = GetTimestamp(),
                Data = _cachedNv12
            };
        }

        // Render new frame at the requested resolution
        var bgra = RenderPlaceholderBgra32(width, height);
        _cachedNv12 = NV12Converter.ConvertBgra32ToNv12(bgra, width, height);
        _cachedWidth = width;
        _cachedHeight = height;

        return new NV12Frame
        {
            Width = width,
            Height = height,
            PtsUs = GetTimestamp(),
            Data = _cachedNv12
        };
    }

    private static byte[] RenderPlaceholderBgra32(int width, int height)
    {
        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        // Dark grey background (matches VS Code dark theme)
        g.Clear(Color.FromArgb(30, 30, 30));

        // Central text
        using var font = new Font("Segoe UI", 48, FontStyle.Regular);
        using var whiteBrush = new SolidBrush(Color.White);
        using var subFont = new Font("Segoe UI", 20, FontStyle.Regular);
        using var greyBrush = new SolidBrush(Color.FromArgb(150, 150, 150));

        var mainText = "📱 Connect your iPhone";
        var subText = "Universal Camera";

        var mainSize = g.MeasureString(mainText, font);
        var subSize = g.MeasureString(subText, subFont);

        float mainX = (width - mainSize.Width) / 2;
        float mainY = (height - mainSize.Height) / 2 - 30;

        float subX = (width - subSize.Width) / 2;
        float subY = mainY + mainSize.Height + 20;

        g.DrawString(mainText, font, whiteBrush, mainX, mainY);
        g.DrawString(subText, subFont, greyBrush, subX, subY);

        // Convert Bitmap to BGRA32 byte array
        var data = bmp.LockBits(new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        byte[] bgra = new byte[width * height * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
        bmp.UnlockBits(data);

        return bgra;
    }

    private static long GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public void Dispose()
    {
        _cachedNv12 = null;
    }
}
