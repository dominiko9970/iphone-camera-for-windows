using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Shared memory IPC bridge for DirectShow virtual camera filter.
/// Writes NV12 frames to a named memory-mapped file so the C++ DLL can read them.
/// </summary>
public sealed class SharedMemoryBridge : IDisposable
{
    private const string MemoryMappedFileName = "UniversalCam_VCam_Frame";
    private const int MaxFrameSize = 3840 * 2160 * 2;  // Upper bound for 4K NV12
    private const int HeaderSize = 64;  // Enough for metadata
    private const int TotalSize = HeaderSize + MaxFrameSize;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private readonly object _lock = new();
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FrameHeader
    {
        public int Width;
        public int Height;
        public int Stride;
        public int DataSize;
        public long SequenceNumber;
        public long PtsUs;
    }

    /// <summary>
    /// Writes an NV12 frame to the shared memory buffer.
    /// </summary>
    public void WriteFrame(NV12Frame frame)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SharedMemoryBridge));

        lock (_lock)
        {
            EnsureOpen();

            if (frame.Data.Length > MaxFrameSize)
                throw new ArgumentException($"Frame too large: {frame.Data.Length} > {MaxFrameSize}");

            var header = new FrameHeader
            {
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Width,
                DataSize = frame.Data.Length,
                SequenceNumber = Environment.TickCount64,
                PtsUs = frame.PtsUs
            };

            // Write header
            int headerBytes = Marshal.SizeOf(header);
            unsafe
            {
                fixed (byte* pHeader = new byte[headerBytes])
                {
                    Marshal.StructureToPtr(header, (IntPtr)pHeader, false);
                    for (int i = 0; i < headerBytes; i++)
                        _accessor!.Write(i, pHeader[i]);
                }
            }

            // Write frame data
            _accessor!.WriteArray(HeaderSize, frame.Data, 0, frame.Data.Length);
        }
    }

    private void EnsureOpen()
    {
        if (_mmf != null)
            return;

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MemoryMappedFileName);
        }
        catch (FileNotFoundException)
        {
            _mmf = MemoryMappedFile.CreateNew(MemoryMappedFileName, TotalSize);
        }

        _accessor = _mmf.CreateViewAccessor();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _accessor?.Dispose();
            _mmf?.Dispose();
            _disposed = true;
        }
    }
}
