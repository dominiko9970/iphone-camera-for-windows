using NAudio.CoreAudioApi;
using NAudio.Wave;
using UniversalCam.Core.Audio;

namespace UniversalCam.Audio;

/// <summary>Represents a WASAPI render endpoint.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Plays decoded PCM audio via WASAPI shared mode.
/// Feed <see cref="PcmFrame"/> objects from <see cref="Core.Audio.AacDecoder.PcmDecoded"/>.
///
/// Thread safety: Feed() may be called from any thread.
/// WASAPI is initialised lazily on the first frame so we never open an audio
/// device when no audio arrives (e.g. iPhone has mic disabled).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut?            _out;
    private BufferedWaveProvider? _buffer;
    private readonly object       _initLock = new();
    private bool    _started;
    private string? _deviceId;

    // ── Volume / mute ─────────────────────────────────────────────────────────

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_out is not null)
                _out.Volume = value ? 0f : 1f;
        }
    }

    /// RMS level of the last decoded chunk (0.0 – 1.0). Updated on each Feed() call.
    /// Written from the transport thread; read from a UI timer — float write is atomic on x64.
    public float CurrentRms { get; private set; }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns all active WASAPI render endpoints.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    /// <summary>
    /// Selects a specific output device by its WASAPI ID.
    /// Pass null to revert to the default device.
    /// Takes effect on the next frame if already playing.
    /// </summary>
    public void SetDevice(string? deviceId)
    {
        lock (_initLock)
        {
            _deviceId = deviceId;
            if (_started)
            {
                _out?.Stop();
                _out?.Dispose();
                _out     = null;
                _buffer  = null;
                _started = false;
            }
        }
    }

    public void Feed(PcmFrame frame)
    {
        EnsureStarted(frame.Rate, frame.Channels);
        UpdateRms(frame.Data);
        _buffer!.AddSamples(frame.Data, 0, frame.Data.Length);
    }

    public void Dispose()
    {
        _out?.Stop();
        _out?.Dispose();
        _buffer = null;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void EnsureStarted(int rate, int channels)
    {
        if (_started) return;
        lock (_initLock)
        {
            if (_started) return;

            var format = new WaveFormat(rate, 16, channels);
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration          = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };

            if (_deviceId != null)
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(_deviceId);
                _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
            }
            else
            {
                _out = new WasapiOut(AudioClientShareMode.Shared, 80);
            }

            _out.Init(_buffer);
            _out.Volume = _isMuted ? 0f : 1f;
            _out.Play();
            _started = true;
        }
    }

    private void UpdateRms(byte[] data)
    {
        int count = data.Length / 2;
        if (count == 0) return;
        float sumSq = 0f;
        for (int i = 0; i < data.Length - 1; i += 2)
        {
            float norm = BitConverter.ToInt16(data, i) / 32768f;
            sumSq += norm * norm;
        }
        CurrentRms = MathF.Sqrt(sumSq / count);
    }
}
