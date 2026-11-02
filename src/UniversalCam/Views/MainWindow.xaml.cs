using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UniversalCam.Audio;
using UniversalCam.Core;
using UniversalCam.Core.Audio;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;
using UniversalCam.Core.Video;
using UniversalCam.VirtualCamera;

namespace UniversalCam.Views;

// ── Value converters ──────────────────────────────────────────────────────────

public sealed class BoolToHighlightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C))
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToAccentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))   // iOS blue
            : new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));  // iOS tertiary

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// ── Camera view-model ─────────────────────────────────────────────────────────

public sealed class CameraViewModel
{
    public string Id          { get; set; } = string.Empty;
    public string Name        { get; set; } = string.Empty;
    public string Position    { get; set; } = string.Empty;
    public string CameraType  { get; set; } = string.Empty;
    public double ZoomFactor  { get; set; } = 1.0;
    public bool   IsSelected  { get; set; }

    public string DisplayName
    {
        get
        {
            string z = ZoomFactor.ToString("0.#");
            return (CameraType, Position) switch
            {
                ("ultra_wide", _)    => $"Ultra Wide {z}×",
                ("telephoto",  _)    => $"Telephoto {z}×",
                ("true_depth", _)    => $"Selfie {z}×",
                (_,         "front") => $"Selfie {z}×",
                _                    => $"Wide {z}×",
            };
        }
    }
}

// ── MainWindow ────────────────────────────────────────────────────────────────

public partial class MainWindow : Window
{
    private ConnectionManager? _cm;
    private H264Decoder? _decoder;
    private AacDecoder? _aacDecoder;
    private AudioPlayer? _audioPlayer;
    private VirtualCameraSession? _vCamSession;

    private int _frameWidth;
    private int _frameHeight;
    private WriteableBitmap? _bitmap;
    private int _rotation;

    // ── Render-level drop guard: skip Dispatcher if a frame is already queued ──
    private int _pendingFrame;

    // ── Decode queue: all frames enqueued in order so H.264 P-frame chain is intact ──
    private readonly System.Collections.Concurrent.ConcurrentQueue<MediaFrame> _videoQueue = new();
    private const int MaxVideoQueueDepth = 32;
    private int _decoderRunning;
    private int _waitingForVideoKeyframe;

    // ── iPhone audio toggle ────────────────────────────────────────────────────
    private bool _phoneAudioEnabled = true;

    private string _currentCameraId = string.Empty;

    private CancellationTokenSource? _configCts;

    private readonly DispatcherTimer _vuTimer    = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _pingTimer  = new() { Interval = TimeSpan.FromSeconds(1) };

    // Fix 5C: frame-alive pulse on status dot
    private readonly DispatcherTimer _framePulseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long _lastFrameReceivedTick;
    private bool _pulseState;

    // Fix 5A: FPS tracking (UI-thread-only fields)
    private int  _fpsCount;
    private long _lastFpsTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private int  _lastFps;

    // Fix 3: zoom level
    private double _framingZoom = 1.0;

    // Portrait framing: crop position (0.0 = top, 1.0 = bottom, 0.5 = centre)
    private double _portraitCropNorm = 0.5;
    private bool   _isPortrait;

    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded  += OnLoaded;
        Closing += OnClosing;
        Closed  += OnClosed;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _decoder = new H264Decoder();
        _decoder.FrameDecoded += OnFrameDecoded;

        _vCamSession = new VirtualCameraSession();

        _aacDecoder  = new AacDecoder();
        _audioPlayer = new AudioPlayer();
        _aacDecoder.PcmDecoded += (_, pcm) => _audioPlayer.Feed(pcm);

        cbAudioDevice.ItemsSource       = AudioPlayer.GetOutputDevices();
        cbAudioDevice.DisplayMemberPath = "Name";
        if (cbAudioDevice.Items.Count > 0)
            cbAudioDevice.SelectedIndex = 0;

        _vuTimer.Tick += (_, _) =>
        {
            if (_audioPlayer is not null && pbVolume.IsEnabled)
                pbVolume.Value = _audioPlayer.CurrentRms;
        };
        _vuTimer.Start();

        _pingTimer.Tick += (_, _) => _ = _cm?.PingAsync();

        // Fix 5C: pulse the connection dot when frames are arriving
        _framePulseTimer.Tick += (_, _) =>
        {
            bool alive = _cm?.State == TransportState.Connected &&
                         Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastFrameReceivedTick) < 500;
            if (alive) { _pulseState = !_pulseState; ellStatus.Opacity = _pulseState ? 1.0 : 0.45; }
            else ellStatus.Opacity = 1.0;
        };
        _framePulseTimer.Start();

        _cm = new ConnectionManager();
        _cm.StateChanged    += OnStateChanged;
        _cm.FrameReceived   += OnMediaFrame;
        _cm.CamerasReceived += OnCamerasReceived;
        _cm.LatencyUpdated  += OnLatencyUpdated;
        _cm.CameraSwitched      += (_, _) => _decoder?.RequestReset();
        _cm.OrientationChanged  += OnOrientationChanged;

        _decoder.FrameDecoded += _vCamSession.OnFrameDecoded;
        _cm.StateChanged      += _vCamSession.OnConnectionStateChanged;

        txtIpHint.Text = GetLocalIpHint();
        UpdateStatus(TransportState.Idle, TransportType.None);

        try   { await _cm.StartAsync(); }
        catch (Exception ex) { txtWaiting.Text = $"Startup error: {ex.Message}"; }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true;
        Hide();
    }

    internal void ForceClose()
    {
        _reallyClosing = true;
        Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _pingTimer.Stop();
        _vuTimer.Stop();
        _framePulseTimer.Stop();
        _configCts?.Cancel();

        while (_videoQueue.TryDequeue(out _)) { }
        while (System.Threading.Interlocked.CompareExchange(ref _decoderRunning, 0, 0) != 0)
            await Task.Delay(5);

        if (_cm is not null)
        {
            await _cm.StopStreamAsync();
            await _cm.DisposeAsync();
            _cm = null;
        }

        _vCamSession?.Dispose();
        _decoder?.Dispose();
        _aacDecoder?.Dispose();
        _audioPlayer?.Dispose();
    }

    // ── Transport events ──────────────────────────────────────────────────────

    private void OnStateChanged(object? sender, TransportState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var type = _cm?.ActiveTransport ?? TransportType.None;
            UpdateStatus(state, type);

            if (state == TransportState.Connected)
            {
                _rotation = 0;
                imgRotation.Angle = 0;
                txtDeviceName.Text = _cm?.DeviceName ?? "iPhone";
                txtWaiting.Text   = "Connected — waiting for stream…";
                btnMute.IsEnabled = true;
                pbVolume.IsEnabled = true;
                _pingTimer.Start();
                _configCts?.Cancel();
                _configCts = new CancellationTokenSource();
                var cts = _configCts;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(120, cts.Token); }
                    catch (OperationCanceledException) { return; }
                    await Dispatcher.InvokeAsync(() => _ = SendConfigureAndStartAsync());
                });
            }
            else if (state is TransportState.Disconnected or TransportState.Idle)
            {
                _pingTimer.Stop();
                txtDeviceName.Text = "No device";
                pnlNoStream.Visibility    = Visibility.Visible;
                pnlVideoInfo.Visibility   = Visibility.Collapsed;
                imgPreview.Source         = null;
                icBackCameras.ItemsSource  = null;
                icFrontCameras.ItemsSource = null;
                pnlBack.Visibility   = Visibility.Collapsed;
                pnlFront.Visibility  = Visibility.Collapsed;
                btnMute.IsEnabled    = false;
                pbVolume.IsEnabled   = false;
                pbVolume.Value       = 0;
                txtInfo.Text         = string.Empty;
                txtLatency.Text      = string.Empty;
                txtInfoSep.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnMediaFrame(object? sender, MediaFrame frame)
    {
        if (frame.IsVideo)
        {
            if (_videoQueue.Count >= MaxVideoQueueDepth)
            {
                while (_videoQueue.TryDequeue(out _)) { }
                System.Threading.Interlocked.Exchange(ref _waitingForVideoKeyframe, 1);
                _decoder?.RequestReset();
                Console.WriteLine("[Video] Decode backlog dropped; waiting for keyframe");
            }
            if (System.Threading.Volatile.Read(ref _waitingForVideoKeyframe) != 0)
            {
                if (!frame.Header.IsKeyframe) return;
                System.Threading.Interlocked.Exchange(ref _waitingForVideoKeyframe, 0);
            }
            _videoQueue.Enqueue(frame);
            if (System.Threading.Interlocked.CompareExchange(ref _decoderRunning, 1, 0) == 0)
                System.Threading.Tasks.Task.Run(DrainVideoFrames);
        }
        else if (frame.IsAudio && _phoneAudioEnabled)
        {
            try { _aacDecoder?.Decode(frame.Payload, frame.Header.PtsUs); }
            catch (Exception ex) { Console.WriteLine($"[MainWindow] Audio decode error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void DrainVideoFrames()
    {
        while (_videoQueue.TryDequeue(out var frame))
        {
            try { _decoder?.Feed(frame); }
            catch (Exception ex) { Console.WriteLine($"[MainWindow] Decode error: {ex.GetType().Name}: {ex.Message}"); }
        }
        System.Threading.Interlocked.Exchange(ref _decoderRunning, 0);
        if (!_videoQueue.IsEmpty &&
            System.Threading.Interlocked.CompareExchange(ref _decoderRunning, 1, 0) == 0)
            System.Threading.Tasks.Task.Run(DrainVideoFrames);
    }

    // Fix 1: auto-select first back camera when server sends no current camera ID
    private void OnCamerasReceived(object? sender, AvailableCameras msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var vms = msg.Cameras
                .Select(c => new CameraViewModel
                {
                    Id         = c.Id,
                    Name       = c.Name,
                    Position   = c.Position,
                    CameraType = c.Type,
                    ZoomFactor = c.ZoomFactor,
                    IsSelected = c.Id == msg.CurrentCameraId
                })
                .ToList();

            // Fix 1: if nothing is selected, highlight the first back camera visually
            if (!vms.Any(v => v.IsSelected))
            {
                var first = vms.FirstOrDefault(v => v.Position == "back") ?? vms.FirstOrDefault();
                if (first is not null) { first.IsSelected = true; _currentCameraId = first.Id; }
            }
            else
            {
                _currentCameraId = msg.CurrentCameraId;
            }

            var back  = vms.Where(c => c.Position == "back").ToList();
            var front = vms.Where(c => c.Position is "front" or "unspecified").ToList();

            icBackCameras.ItemsSource  = back;
            icFrontCameras.ItemsSource = front;
            pnlBack.Visibility  = back.Count  > 0 ? Visibility.Visible : Visibility.Collapsed;
            pnlFront.Visibility = front.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_cm is not null && _cm.DeviceName is { Length: > 0 } name)
                txtDeviceName.Text = name;
        });
    }

    private void OnOrientationChanged(object? sender, string orientation)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool portrait = orientation == "portrait";
            if (portrait == _isPortrait) return;
            _isPortrait = portrait;
            pnlPortraitFraming.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;
            if (!portrait)
            {
                _portraitCropNorm      = 0.5;
                sldrPortraitCrop.Value = 0.5;
            }
        });
    }

    private void OnLatencyUpdated(object? sender, int ms)
    {
        Dispatcher.BeginInvoke(() =>
        {
            txtLatency.Text = $"~{ms} ms";
            txtInfoSep.Visibility = txtInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ── Decoder output (background thread) ────────────────────────────────────

    private void OnFrameDecoded(object? sender, DecodedFrame frame)
    {
        // Fix 5C: record timestamp so the pulse timer knows frames are alive
        System.Threading.Volatile.Write(ref _lastFrameReceivedTick, Environment.TickCount64);

        if (System.Threading.Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0)
            return;

        // Portrait crop: extract a landscape 16:9 slice at the user-chosen pan position.
        // This runs on the decoder thread (before Dispatcher) to avoid blocking the UI.
        int   dispW    = frame.Width;
        int   dispH    = frame.Height;
        byte[] dispData = frame.Data;
        bool  portrait = frame.Height > frame.Width;

        if (portrait)
        {
            int cropH  = (int)(frame.Width * 9.0 / 16.0);
            cropH      = Math.Min(cropH, frame.Height);
            int maxY   = frame.Height - cropH;
            int cropY  = (int)(_portraitCropNorm * maxY);
            cropY      = Math.Clamp(cropY, 0, maxY);

            dispW    = frame.Width;
            dispH    = cropH;
            dispData = new byte[dispW * dispH * 4];
            Buffer.BlockCopy(frame.Data, cropY * frame.Width * 4, dispData, 0, dispData.Length);
        }

        Dispatcher.BeginInvoke(() =>
        {
            System.Threading.Interlocked.Exchange(ref _pendingFrame, 0);

            if (dispW <= 1) return;

            // Show/hide the portrait framing panel
            if (portrait != _isPortrait)
            {
                _isPortrait = portrait;
                pnlPortraitFraming.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;
            }
            if (portrait) UpdatePortraitOverlay(frame.Height, dispH);

            pnlNoStream.Visibility  = Visibility.Collapsed;
            pnlVideoInfo.Visibility = Visibility.Visible;

            if (_bitmap is null || _frameWidth != dispW || _frameHeight != dispH)
            {
                _frameWidth  = dispW;
                _frameHeight = dispH;
                _bitmap = new WriteableBitmap(
                    dispW, dispH, 96, 96,
                    PixelFormats.Bgra32, null);
                imgPreview.Source = _bitmap;
            }

            _bitmap.Lock();
            try
            {
                Marshal.Copy(dispData, 0, _bitmap.BackBuffer, dispData.Length);
                _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, dispW, dispH));
            }
            finally
            {
                _bitmap.Unlock();
            }

            // Fix 5A: FPS counter — updated every second
            _fpsCount++;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastFpsTime >= 1000)
            {
                _lastFps     = (int)Math.Round(_fpsCount * 1000.0 / (nowMs - _lastFpsTime));
                _fpsCount    = 0;
                _lastFpsTime = nowMs;
            }
            txtInfo.Text = _lastFps > 0
                ? $"{_frameWidth}×{_frameHeight}  {_lastFps} fps"
                : $"{_frameWidth}×{_frameHeight}";
            txtInfoSep.Visibility = txtLatency.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ── Button / control handlers ─────────────────────────────────────────────

    // Fix 2B: optimistic camera selection — highlight immediately, confirm via server
    private async void OnCameraRowClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: string id } || _cm is null) return;
            UpdateCameraSelection(id);
            await _cm.SwitchCameraAsync(id);
            await Task.Delay(300);
            await _cm.ListCamerasAsync();
        }
        catch (Exception ex) { Log(ex); }
    }

    private void OnQualityChanged(object sender, RoutedEventArgs e)
    {
        if (_cm?.State == TransportState.Connected || _cm?.State == (TransportState)4)
            _ = SendConfigureAndStartAsync();
    }

    private void OnRotateClicked(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        imgRotation.Angle = _rotation;
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer is null) return;
        _audioPlayer.IsMuted = btnMute.IsChecked ?? false;
    }

    private void OnPhoneAudioToggled(object sender, RoutedEventArgs e)
    {
        _phoneAudioEnabled = chkPhoneAudio.IsChecked ?? true;
        if (_audioPlayer is null) return;
        _audioPlayer.IsMuted = !_phoneAudioEnabled || (btnMute.IsChecked ?? false);
    }

    private void OnAudioDeviceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cbAudioDevice.SelectedItem is Audio.AudioDeviceInfo dev)
            _audioPlayer?.SetDevice(dev.Id);
    }

    // Fix 3: framing / zoom handlers
    private void ApplyZoom(double zoom)
    {
        _framingZoom      = zoom;
        imgZoom.ScaleX    = zoom;
        imgZoom.ScaleY    = zoom;
        if (txtZoomLevel is not null) txtZoomLevel.Text = $"{zoom:0.0}×";
        sldrZoom.Value    = zoom;
    }

    private void OnZoomChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (imgZoom is null || txtZoomLevel is null) return; // guard during InitializeComponent
        try { ApplyZoom(Math.Round(e.NewValue, 2)); }
        catch (Exception ex) { Log(ex); }
    }

    private void OnFrameFull(object sender, RoutedEventArgs e) { try { ApplyZoom(1.0);  } catch (Exception ex) { Log(ex); } }
    private void OnFrame43  (object sender, RoutedEventArgs e) { try { ApplyZoom(1.33); } catch (Exception ex) { Log(ex); } }
    private void OnFrame169 (object sender, RoutedEventArgs e) { try { ApplyZoom(1.0);  } catch (Exception ex) { Log(ex); } }

    private void OnPortraitCropChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        try { _portraitCropNorm = e.NewValue; }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>
    /// Moves the crop-window rectangle inside the portrait preview canvas.
    /// srcH = original portrait height (e.g. 1920), cropH = landscape slice height (e.g. 607).
    /// </summary>
    private void UpdatePortraitOverlay(int srcH, int cropH)
    {
        const double canvasH = 72.0;
        double winH   = canvasH * cropH / srcH;
        double maxTop = canvasH - winH;
        double top    = _portraitCropNorm * maxTop;

        System.Windows.Controls.Canvas.SetTop(rctCropWindow, top);
        rctCropWindow.Height = winH;

        // Dim the areas outside the crop window
        rctPortraitAbove.Height = top;
        System.Windows.Controls.Canvas.SetTop(rctPortraitBelow, top + winH);
        rctPortraitBelow.Height = canvasH - top - winH;
    }

    // Fix 5B: copy IP to clipboard
    private void OnCopyIpClicked(object sender, RoutedEventArgs e)
    {
        var text = txtIpHint.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        Clipboard.SetText(text.StartsWith("PC: ") ? text[4..] : text);
    }

    // Fix 5D: keyboard shortcuts (M = mute, R = rotate)
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.M && btnMute.IsEnabled)
        {
            btnMute.IsChecked = !btnMute.IsChecked;
            OnMuteClicked(btnMute, new RoutedEventArgs());
        }
        else if (e.Key == Key.R)
        {
            OnRotateClicked(btnRotate, new RoutedEventArgs());
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs an exception to Console — which TeeWriter forwards to logs/run_latest.txt.
    /// Calling from event handlers prevents exceptions from bubbling to DispatcherUnhandledException,
    /// so the VS Code debugger won't pause and the full trace is always in the log file.
    /// </summary>
    private static void Log(Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => Console.WriteLine($"[{caller}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    // Fix 2B: immediately rebuild camera ItemsSources with new selection (no INPC on CameraViewModel)
    private void UpdateCameraSelection(string id)
    {
        _currentCameraId = id;

        void Reselect(System.Windows.Controls.ItemsControl ic)
        {
            if (ic.ItemsSource is not IEnumerable<CameraViewModel> items) return;
            var list = items.ToList();
            foreach (var vm in list) vm.IsSelected = vm.Id == id;
            ic.ItemsSource = list; // reassign forces WPF to re-evaluate bindings
        }

        Reselect(icBackCameras);
        Reselect(icFrontCameras);
    }

    private async Task SendConfigureAndStartAsync()
    {
        if (_cm is null) return;

        var (res, fps, bitrate) = true switch
        {
            _ when rb720p60.IsChecked  == true => ("1280x720",  60,  7_000_000),
            _ when rb1080p60.IsChecked == true => ("1920x1080", 60, 16_000_000),
            _ when rb4K.IsChecked      == true => ("3840x2160", 30, 25_000_000),
            _ when rb4K60.IsChecked    == true => ("3840x2160", 60, 50_000_000),
            _                                   => ("1920x1080", 30,  8_000_000),
        };

        await _cm.ConfigureAsync(res, fps, "h264", bitrate);
    }

    private static string GetLocalIpHint()
    {
        try
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
            return ip is null ? string.Empty : $"PC: {ip}";
        }
        catch { return string.Empty; }
    }

    private void UpdateStatus(TransportState state, TransportType type)
    {
        (ellStatus.Fill, txtStatus.Text, txtStatus.Foreground) = state switch
        {
            TransportState.Idle         => (new SolidColorBrush(Color.FromRgb(0x48,0x48,0x4A)), "Idle",          new SolidColorBrush(Color.FromRgb(0x63,0x63,0x66))),
            TransportState.Listening    => (new SolidColorBrush(Color.FromRgb(0x0A,0x84,0xFF)), "Searching…",    new SolidColorBrush(Color.FromRgb(0x0A,0x84,0xFF))),
            TransportState.Connected    => (new SolidColorBrush(Color.FromRgb(0x30,0xD1,0x58)), "Connected",     new SolidColorBrush(Color.FromRgb(0x30,0xD1,0x58))),
            TransportState.Disconnected => (new SolidColorBrush(Color.FromRgb(0xFF,0x9F,0x0A)), "Disconnected",  new SolidColorBrush(Color.FromRgb(0xFF,0x9F,0x0A))),
            TransportState.Error        => (new SolidColorBrush(Color.FromRgb(0xFF,0x45,0x3A)), "Error",         new SolidColorBrush(Color.FromRgb(0xFF,0x45,0x3A))),
            _                           => (new SolidColorBrush(Color.FromRgb(0x48,0x48,0x4A)), state.ToString(), new SolidColorBrush(Color.FromRgb(0x63,0x63,0x66))),
        };

        if (type == TransportType.None)
        {
            badgeTransport.Visibility = Visibility.Collapsed;
        }
        else
        {
            badgeTransport.Visibility = Visibility.Visible;
            badgeTransport.Background = type == TransportType.USB
                ? (Brush)Application.Current.Resources["UsbBadgeBrush"]
                : (Brush)Application.Current.Resources["WifiBadgeBrush"];
            txtTransport.Text = type.ToString();
        }
    }
}
