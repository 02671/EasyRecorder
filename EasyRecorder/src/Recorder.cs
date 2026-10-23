using System.Diagnostics;
using ScreenRecorderLib;

namespace EasyRecorder;

public sealed class Recorder : IDisposable
{
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

    public Task StartAsync(Rectangle region, string outputFolder, AudioMode audioMode)
    {
        if (IsRecording)
        {
            return Task.CompletedTask;
        }

        region = NormalizeRegion(region);
        if (region.Width < 16 || region.Height < 16)
        {
            throw new InvalidOperationException("录制区域太小，请重新框选。");
        }

        Directory.CreateDirectory(outputFolder);
        _outputFolder = outputFolder;
        _startedAt = DateTime.Now;
        _outputPath = GetPlannedOutputPath();
        Warning = null;
        _failure = null;
        _paused = false;
        _clock.Reset();

        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = started;
        _stopped = null;

        try
        {
            ScreenRecorderLib.Recorder engine = ScreenRecorderLib.Recorder.CreateRecorder(BuildOptions(region, audioMode));
            engine.OnRecordingComplete += OnRecordingComplete;
            engine.OnRecordingFailed += OnRecordingFailed;
            engine.OnStatusChanged += OnStatusChanged;
            _engine = engine;
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

        return WaitUntilStartedAsync(started);
    }

    private async Task WaitUntilStartedAsync(TaskCompletionSource<bool> started)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            await started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await AbortStartAsync().ConfigureAwait(false);
            throw new TimeoutException("录制启动超时。");
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

            if (!IsRecording)
            {
                IsRecording = true;
                if (!_clock.IsRunning)
                {
                    _clock.Start();
                }
            }
        }
    }

    public void Pause()
    {
        if (!IsRecording || _paused)
        {
            return;
        }

        try
        {
            _engine?.Pause();
        }
        catch
        {
        }

        _paused = true;
        _clock.Stop();
    }

    public void Resume()
    {
        if (!IsRecording || !_paused)
        {
            return;
        }

        try
        {
            _engine?.Resume();
        }
        catch
        {
        }

        _paused = false;
        _clock.Start();
    }

    public async Task<string?> StopAsync(bool discard)
    {
        ScreenRecorderLib.Recorder? engine;
        TaskCompletionSource<string?> stopped;

        lock (_gate)
        {
            if (!IsRecording && _engine == null)
            {
                return null;
            }

            IsRecording = false;
            engine = _engine;
            stopped = _stopped ?? new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = stopped;
        }

        _clock.Stop();

        if (engine == null)
        {
            return null;
        }

        try
        {
            engine.Stop();
        }
        catch (Exception ex)
        {
            ReleaseEngine();
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
            if (discard)
            {
                TryDeleteFile(_outputPath);
                return null;
            }

            throw new TimeoutException("保存录像超时。");
        }

        ReleaseEngine();

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

    private RecorderOptions BuildOptions(Rectangle region, AudioMode audioMode)
    {
        bool includeAudio = audioMode != AudioMode.None;
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = CreateSources()
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(region.Width, region.Height),
                SourceRect = new ScreenRect(region.X, region.Y, region.Width, region.Height),
                Stretch = StretchMode.Fill
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
                Bitrate = ChooseBitrate(region.Width, region.Height),
                Framerate = 30,
                IsFixedFramerate = true,
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main
                },
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = true,
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

    private static List<RecordingSourceBase> CreateSources()
    {
        List<RecordingSourceBase> sources = new();
        try
        {
            foreach (var display in ScreenRecorderLib.Recorder.GetDisplays())
            {
                sources.Add(new DisplayRecordingSource(display));
            }
        }
        catch
        {
        }

        if (sources.Count == 0)
        {
            DisplayRecordingSource main = DisplayRecordingSource.MainMonitor;
            if (main != null)
            {
                sources.Add(main);
            }
        }

        return sources;
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
                _paused = true;
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
        _failure = message;
        lock (_gate)
        {
            IsRecording = false;
        }

        _clock.Stop();
        _started?.TrySetException(new InvalidOperationException(message));
        if (_stopped != null)
        {
            _stopped.TrySetResult(e.FilePath);
        }
        else
        {
            RaiseWarning(message);
        }
    }

    private async Task AbortStartAsync()
    {
        ScreenRecorderLib.Recorder? engine = _engine;
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
        long bitrate = (long)pixels * 4;
        if (bitrate < 2_000_000)
        {
            bitrate = 2_000_000;
        }
        else if (bitrate > 12_000_000)
        {
            bitrate = 12_000_000;
        }

        return (int)bitrate;
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
