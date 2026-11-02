using System;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Converts BGRA32 frames to NV12 format using BT.601 full-range coefficients.
/// This matches the existing color space in H264Decoder.
/// </summary>
public static class NV12Converter
{
    /// <summary>
    /// Converts BGRA32 frame data to NV12 format.
    /// </summary>
    /// <param name="bgra">BGRA32 frame bytes (width × height × 4)</param>
    /// <param name="width">Frame width in pixels</param>
    /// <param name="height">Frame height in pixels</param>
    /// <returns>NV12 frame bytes (width × height × 1.5)</returns>
    public static byte[] ConvertBgra32ToNv12(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException($"Invalid BGRA buffer size: expected {width * height * 4}, got {bgra.Length}");

        // NV12 = Y plane + UV plane
        // Y plane: width × height
        // UV plane: (width/2) × (height/2) × 2 (interleaved U/V)
        int ySize = width * height;
        int uvSize = ySize >> 1;  // width/2 × height/2 × 2
        byte[] nv12 = new byte[ySize + uvSize];

        unsafe
        {
            fixed (byte* pBgra = bgra, pNv12 = nv12)
            {
                // Process Y plane and sample U/V for every 2×2 block
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int bgraIdx = (y * width + x) * 4;
                        int b = pBgra[bgraIdx + 0];
                        int g = pBgra[bgraIdx + 1];
                        int r = pBgra[bgraIdx + 2];

                        // BT.601 full-range: Y = 0.299*R + 0.587*G + 0.114*B
                        int yVal = (19595 * r + 38470 * g + 7471 * b) >> 16;
                        pNv12[y * width + x] = (byte)yVal.Clamp(0, 255);

                        // Sample U/V only for even rows and columns
                        if ((y & 1) == 0 && (x & 1) == 0)
                        {
                            // Sample 2×2 block and average
                            int b00 = pBgra[bgraIdx + 0];
                            int g00 = pBgra[bgraIdx + 1];
                            int r00 = pBgra[bgraIdx + 2];

                            int b10 = x + 1 < width ? pBgra[bgraIdx + 4 + 0] : b00;
                            int g10 = x + 1 < width ? pBgra[bgraIdx + 4 + 1] : g00;
                            int r10 = x + 1 < width ? pBgra[bgraIdx + 4 + 2] : r00;

                            int b01Idx = y + 1 < height ? bgraIdx + width * 4 : bgraIdx;
                            int b01 = pBgra[b01Idx + 0];
                            int g01 = pBgra[b01Idx + 1];
                            int r01 = pBgra[b01Idx + 2];

                            int b11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 0] : b01;
                            int g11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 1] : g01;
                            int r11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 2] : r01;

                            int rAvg = (r00 + r10 + r01 + r11) >> 2;
                            int gAvg = (g00 + g10 + g01 + g11) >> 2;
                            int bAvg = (b00 + b10 + b01 + b11) >> 2;

                            // BT.601 full-range:
                            // U = (B - Y) / 1.772 = -0.168*R - 0.331*G + 0.5*B + 128
                            // V = (R - Y) / 1.402 = 0.5*R - 0.419*G - 0.081*B + 128
                            int uVal = ((-9714 * rAvg - 19070 * gAvg + 28784 * bAvg) >> 16) + 128;
                            int vVal = ((28784 * rAvg - 24107 * gAvg - 4677 * bAvg) >> 16) + 128;

                            int uvIdx = ySize + (y >> 1) * width + x;
                            pNv12[uvIdx + 0] = (byte)uVal.Clamp(0, 255);
                            pNv12[uvIdx + 1] = (byte)vVal.Clamp(0, 255);
                        }
                    }
                }
            }
        }

        return nv12;
    }
}

file static class IntExtensions
{
    public static int Clamp(this int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
