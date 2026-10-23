using System.Diagnostics;
using ScreenRecorderLib;

namespace EasyRecorder;

public sealed class Recorder : IDisposable
{
    private const long MinimumFreeBytes = 500L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();

    private ScreenRecorderLib.Recorder? _engine;
    private TaskCompletionSource<bool>? _started;
    private TaskCompletionSource<string?>? _stopped;
    private string _outputFolder = string.Empty;
    private string _outputPath = string.Empty;
    private DateTime _startedAt;
    private bool _paused;
    private bool _disposed;
    private string? _failure;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? Warning { get; private set; }

    public event Action<string>? WarningRaised;

    public async Task StartAsync(Rectangle region, string outputFolder, AudioMode audioMode)
    {
        if (IsRecording)
        {
            return;
        }

        region = NormalizeRegion(region);
        if (region.Width < 16 || region.Height < 16)
        {
            throw new InvalidOperationException("录制区域太小，请重新框选。");
        }

        Directory.CreateDirectory(outputFolder);
        EnsureDiskSpace(outputFolder);
        _outputFolder = outputFolder;
        _startedAt = DateTime.Now;
        _outputPath = GetPlannedOutputPath();
        Warning = null;
        _failure = null;
        _paused = false;
        _clock.Reset();

        Exception? hardwareError = null;
        try
        {
            await StartEngineAsync(region, audioMode, true).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsEncoderFailure(ex))
        {
            hardwareError = ex;
            await AbortStartAsync().ConfigureAwait(false);
        }

        try
        {
            await StartEngineAsync(region, audioMode, false).ConfigureAwait(false);
        }
        catch
        {
            await AbortStartAsync().ConfigureAwait(false);
            throw hardwareError ?? new InvalidOperationException("无法开始录制。");
        }
    }

    private async Task StartEngineAsync(Rectangle region, AudioMode audioMode, bool hardware)
    {
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _started = started;
            _stopped = null;
            _failure = null;
        }

        try
        {
            ScreenRecorderLib.Recorder engine = ScreenRecorderLib.Recorder.CreateRecorder(
                BuildOptions(region, audioMode, hardware));
            engine.OnRecordingComplete += OnRecordingComplete;
            engine.OnRecordingFailed += OnRecordingFailed;
            engine.OnStatusChanged += OnStatusChanged;
            lock (_gate)
            {
                _engine = engine;
            }

            engine.Record(_outputPath);
        }
        catch (DllNotFoundException)
        {
            ReleaseEngine();
            throw new InvalidOperationException(
                "录制组件无法加载。请安装 Microsoft Visual C++ Redistributable（x64）后重试。");
        }
        catch (Exception ex)
        {
            ReleaseEngine();
            throw new InvalidOperationException(FriendlyStartError(ex), ex);
        }

        await WaitUntilStartedAsync(started).ConfigureAwait(false);
    }

    private async Task WaitUntilStartedAsync(TaskCompletionSource<bool> started)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(_failure))
            {
                await AbortStartAsync().ConfigureAwait(false);
                throw new InvalidOperationException(_failure);
            }
        }
        catch
        {
            await AbortStartAsync().ConfigureAwait(false);
            throw;
        }

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_failure))
            {
                throw new InvalidOperationException(_failure);
            }

            IsRecording = true;
            _paused = false;
            if (!_clock.IsRunning)
            {
                _clock.Start();
            }
        }
    }

    public void Pause()
    {
        ScreenRecorderLib.Recorder? engine;
        lock (_gate)
        {
            if (!IsRecording || _paused)
            {
                return;
            }

            engine = _engine;
        }

        if (engine == null)
        {
            return;
        }

        try
        {
            engine.Pause();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法暂停录制。", ex);
        }

        lock (_gate)
        {
            _paused = true;
        }

        _clock.Stop();
    }

    public void Resume()
    {
        ScreenRecorderLib.Recorder? engine;
        lock (_gate)
        {
            if (!IsRecording || !_paused)
            {
                return;
            }

            engine = _engine;
        }

        if (engine == null)
        {
            return;
        }

        try
        {
            engine.Resume();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法继续录制。", ex);
        }

        lock (_gate)
        {
            _paused = false;
        }

        _clock.Start();
    }

    public async Task<string?> StopAsync(bool discard)
    {
        ScreenRecorderLib.Recorder? engine;
        TaskCompletionSource<string?> stopped;

        lock (_gate)
        {
            engine = _engine;
            if (engine == null)
            {
                IsRecording = false;
                return null;
            }

            stopped = _stopped ?? new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = stopped;
        }

        _clock.Stop();

        try
        {
            engine.Stop();
        }
        catch (Exception ex)
        {
            ReleaseEngine();
            lock (_gate)
            {
                IsRecording = false;
            }

            if (discard)
            {
                TryDeleteFile(_outputPath);
                return null;
            }

            throw new InvalidOperationException("结束录制失败：" + ex.Message, ex);
        }

        string? path;
        try
        {
            path = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ReleaseEngine();
            lock (_gate)
            {
                IsRecording = false;
            }

            if (discard)
            {
                TryDeleteFile(_outputPath);
                return null;
            }

            throw new TimeoutException("保存录像超时。");
        }

        ReleaseEngine();
        lock (_gate)
        {
            IsRecording = false;
            _paused = false;
        }

        if (discard)
        {
            TryDeleteFile(path);
            TryDeleteFile(_outputPath);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_failure))
        {
            TryDeleteFile(path);
            TryDeleteFile(_outputPath);
            throw new InvalidOperationException(_failure);
        }

        string finalPath = string.IsNullOrWhiteSpace(path) ? _outputPath : path;
        if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
        {
            throw new InvalidOperationException("录像没有成功生成。");
        }

        return finalPath;
    }

    private RecorderOptions BuildOptions(Rectangle region, AudioMode audioMode, bool hardware)
    {
        bool includeAudio = audioMode != AudioMode.None;
        (List<RecordingSourceBase> sources, Size outputSize) = CreateSources(region);
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = sources
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(outputSize.Width, outputSize.Height),
                Stretch = StretchMode.Uniform
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = includeAudio,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = CreateAudioSources(audioMode)
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Bitrate = ChooseBitrate(outputSize.Width, outputSize.Height),
                Framerate = 30,
                IsFixedFramerate = false,
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main
                },
                IsHardwareEncodingEnabled = hardware,
                IsLowLatencyEnabled = false,
                IsMp4FastStartEnabled = true,
                IsFragmentedMp4Enabled = false,
                IsThrottlingDisabled = false
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = true,
                IsMouseClicksDetected = false
            }
        };
    }

    private static (List<RecordingSourceBase> Sources, Size OutputSize) CreateSources(Rectangle region)
    {
        Screen screen = ScreenFromRegion(region);
        DisplayRecordingSource source = CreateDisplaySource(screen);
        Rectangle bounds = screen.Bounds;
        Rectangle crop = Rectangle.Intersect(region, bounds);
        crop.Width -= crop.Width % 2;
        crop.Height -= crop.Height % 2;
        if (crop.Width < 16 || crop.Height < 16)
        {
            crop = new Rectangle(
                bounds.Left,
                bounds.Top,
                bounds.Width - bounds.Width % 2,
                bounds.Height - bounds.Height % 2);
        }

        int x = Math.Max(0, crop.Left - bounds.Left);
        int y = Math.Max(0, crop.Top - bounds.Top);
        bool fullMonitor = x <= 0 && y <= 0 && crop.Width >= bounds.Width - 1 && crop.Height >= bounds.Height - 1;
        if (!fullMonitor)
        {
            source.SourceRect = new ScreenRect(x, y, crop.Width, crop.Height);
            source.OutputSize = new ScreenSize(crop.Width, crop.Height);
        }

        return (new List<RecordingSourceBase> { source }, crop.Size);
    }

    private static Screen ScreenFromRegion(Rectangle region)
    {
        Screen best = Screen.FromRectangle(region);
        int bestArea = IntersectionArea(region, best.Bounds);
        foreach (Screen screen in Screen.AllScreens)
        {
            int area = IntersectionArea(region, screen.Bounds);
            if (area > bestArea)
            {
                best = screen;
                bestArea = area;
            }
        }

        return best;
    }

    private static int IntersectionArea(Rectangle a, Rectangle b)
    {
        Rectangle hit = Rectangle.Intersect(a, b);
        if (hit.IsEmpty)
        {
            return 0;
        }

        return hit.Width * hit.Height;
    }

    private static DisplayRecordingSource CreateDisplaySource(Screen screen)
    {
        try
        {
            foreach (var display in ScreenRecorderLib.Recorder.GetDisplays())
            {
                if (string.Equals(display.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return new DisplayRecordingSource(display);
                }
            }
        }
        catch
        {
        }

        try
        {
            return new DisplayRecordingSource(screen.DeviceName);
        }
        catch
        {
        }

        DisplayRecordingSource main = DisplayRecordingSource.MainMonitor;
        if (main != null)
        {
            return main;
        }

        throw new InvalidOperationException("找不到可录制的显示器。");
    }

    private static List<AudioSourceBase> CreateAudioSources(AudioMode audioMode)
    {
        List<AudioSourceBase> sources = new();
        switch (audioMode)
        {
            case AudioMode.System:
                sources.Add(LoopbackAudioSource.Default);
                break;
            case AudioMode.Microphone:
                sources.Add(CaptureAudioSource.Default);
                break;
            case AudioMode.Both:
                sources.Add(LoopbackAudioSource.Default);
                sources.Add(CaptureAudioSource.Default);
                break;
        }

        return sources;
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        switch (e.Status)
        {
            case RecorderStatus.Recording:
                lock (_gate)
                {
                    IsRecording = true;
                    _paused = false;
                    if (!_clock.IsRunning)
                    {
                        _clock.Start();
                    }
                }

                _started?.TrySetResult(true);
                break;
            case RecorderStatus.Paused:
                lock (_gate)
                {
                    _paused = true;
                }

                _clock.Stop();
                break;
        }
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        string? path = string.IsNullOrWhiteSpace(e.FilePath) ? _outputPath : e.FilePath;
        _started?.TrySetResult(true);
        _stopped?.TrySetResult(path);
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        string message = string.IsNullOrWhiteSpace(e.Error) ? "录制失败。" : e.Error;
        bool duringRecording;
        lock (_gate)
        {
            _failure = message;
            duringRecording = IsRecording && _stopped == null;
            IsRecording = false;
            _paused = false;
        }

        _clock.Stop();
        _started?.TrySetException(new InvalidOperationException(message));
        if (_stopped != null)
        {
            _stopped.TrySetResult(e.FilePath);
            return;
        }

        ReleaseEngine();
        TryDeleteFile(_outputPath);
        if (duringRecording)
        {
            RaiseWarning(message);
        }
    }

    private async Task AbortStartAsync()
    {
        ScreenRecorderLib.Recorder? engine;
        lock (_gate)
        {
            engine = _engine;
        }

        if (engine != null)
        {
            try
            {
                engine.Stop();
            }
            catch
            {
            }
        }

        await Task.Delay(200).ConfigureAwait(false);
        ReleaseEngine();
        TryDeleteFile(_outputPath);
        lock (_gate)
        {
            IsRecording = false;
            _paused = false;
            _failure = null;
            _started = null;
            _stopped = null;
        }

        _clock.Reset();
    }

    private void ReleaseEngine()
    {
        ScreenRecorderLib.Recorder? engine;
        lock (_gate)
        {
            engine = _engine;
            _engine = null;
        }

        if (engine == null)
        {
            return;
        }

        engine.OnRecordingComplete -= OnRecordingComplete;
        engine.OnRecordingFailed -= OnRecordingFailed;
        engine.OnStatusChanged -= OnStatusChanged;
        try
        {
            engine.Dispose();
        }
        catch
        {
        }
    }

    private void RaiseWarning(string message)
    {
        Warning = string.IsNullOrWhiteSpace(Warning) ? message : Warning + "\n" + message;
        try
        {
            WarningRaised?.Invoke(message);
        }
        catch
        {
        }
    }

    private string GetPlannedOutputPath()
    {
        string folder = string.IsNullOrWhiteSpace(_outputFolder)
            ? AppSettings.DefaultOutputFolder()
            : _outputFolder;

        string name = $"录屏_{_startedAt:yyyyMMdd_HHmmss}";
        string candidate = Path.Combine(folder, name + ".mp4");
        int index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{name}_{index}.mp4");
            index++;
        }

        return candidate;
    }

    private static int ChooseBitrate(int width, int height)
    {
        int pixels = Math.Max(1, width) * Math.Max(1, height);
        if (pixels <= 640 * 480)
        {
            return 1_500_000;
        }

        if (pixels <= 1280 * 720)
        {
            return 3_000_000;
        }

        if (pixels <= 1920 * 1080)
        {
            return 6_000_000;
        }

        if (pixels <= 2560 * 1440)
        {
            return 8_000_000;
        }

        return 10_000_000;
    }

    private static void EnsureDiskSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            DriveInfo drive = new(root);
            if (drive.IsReady && drive.AvailableFreeSpace < MinimumFreeBytes)
            {
                throw new InvalidOperationException("磁盘空间不足，请至少保留 500 MB 后再录制。");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static bool IsEncoderFailure(Exception ex)
    {
        string text = (ex.Message + " " + ex.InnerException?.Message) ?? string.Empty;
        return text.Contains("encoder", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("hardware", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MF_", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0xC00D", StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyStartError(Exception ex)
    {
        string text = ex.Message ?? string.Empty;
        if (text.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MF_", StringComparison.OrdinalIgnoreCase))
        {
            return "系统缺少 Media Foundation。Windows N / KN 版请先安装媒体功能包。";
        }

        return string.IsNullOrWhiteSpace(text) ? "无法开始录制。" : text;
    }

    private static Rectangle NormalizeRegion(Rectangle region)
    {
        Rectangle screen = SystemInformation.VirtualScreen;
        region.Intersect(screen);
        region.Width -= region.Width % 2;
        region.Height -= region.Height % 2;
        return region;
    }

    public static Rectangle PrimaryScreenRegion()
    {
        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        return NormalizeRegion(new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _engine?.Stop();
        }
        catch
        {
        }

        ReleaseEngine();
        _clock.Reset();
        IsRecording = false;
    }
}
