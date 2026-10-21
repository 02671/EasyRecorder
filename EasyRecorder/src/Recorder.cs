using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EasyRecorder;

public sealed class Recorder : IDisposable
{
    private const int FramesPerSecond = 30;

    private readonly object _sessionGate = new();
    private readonly object _audioGate = new();
    private readonly object _stopGate = new();
    private readonly Stopwatch _clock = new();
    private readonly ManualResetEventSlim _streamsGo = new(false);

    private FfmpegSession? _standby;
    private FfmpegSession? _active;
    private Task? _prepareTask;
    private CancellationTokenSource? _prepareCts;
    private Task? _videoTask;
    private Task? _audioWriteTask;
    private Task? _audioInitTask;
    private LiveAudioMixer? _mixer;
    private DxgiScreenCapture? _dxgi;
    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _microphoneCapture;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private volatile bool _acceptAudio;
    private volatile bool _audioAbandoned;
    private Rectangle _region;
    private string _outputFolder = string.Empty;
    private string? _captureError;
    private AudioMode _audioMode;
    private DateTime _startedAt;
    private bool _disposed;
    private bool _includeAudio;
    private string _ffmpegPath = string.Empty;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? Warning { get; private set; }

    public event Action<string>? WarningRaised;

    public Task PrepareAsync(string ffmpegPath, Rectangle region, AudioMode audioMode)
    {
        if (_disposed || string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return Task.CompletedTask;
        }

        Rectangle prepared = NormalizeRegion(region);
        Size size = prepared.Size;
        bool includeAudio = audioMode != AudioMode.None;

        _ffmpegPath = ffmpegPath;
        _audioMode = audioMode;
        _region = prepared;

        lock (_sessionGate)
        {
            if (IsRecording)
            {
                return Task.CompletedTask;
            }

            if (_standby is { IsAlive: true, IsReady: true } && _standby.Matches(size, includeAudio))
            {
                return Task.CompletedTask;
            }

            _prepareCts?.Cancel();
            _prepareCts?.Dispose();
            _prepareCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CancellationToken token = _prepareCts.Token;
            string path = ffmpegPath;
            _prepareTask = Task.Run(() => ReplaceStandbyAsync(path, size, includeAudio, token), token);
            return _prepareTask;
        }
    }

    public async Task StartAsync(Rectangle region, string ffmpegPath, string outputFolder, AudioMode audioMode)
    {
        if (IsRecording)
        {
            return;
        }

        await StartCoreAsync(region, ffmpegPath, outputFolder, audioMode).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(Rectangle region, string ffmpegPath, string outputFolder, AudioMode audioMode)
    {
        region = NormalizeRegion(region);
        if (region.Width < 16 || region.Height < 16)
        {
            throw new InvalidOperationException("录制区域太小，请重新框选。");
        }

        Directory.CreateDirectory(outputFolder);
        _region = region;
        _audioMode = audioMode;
        _outputFolder = outputFolder;
        _ffmpegPath = ffmpegPath;
        _startedAt = DateTime.Now;
        _captureError = null;
        Warning = null;
        _stopRequested = false;
        _paused = false;
        _acceptAudio = false;
        _includeAudio = audioMode != AudioMode.None;
        _clock.Reset();
        _streamsGo.Reset();

        FfmpegSession session;
        try
        {
            using CancellationTokenSource startTimeout = new(TimeSpan.FromSeconds(5));
            session = await TakeOrCreateSessionAsync(ffmpegPath, region.Size, _includeAudio, startTimeout.Token).ConfigureAwait(false);
            _active = session;
            _dxgi = DxgiScreenCapture.TryCreate(_region);
            if (_includeAudio)
            {
                _mixer = new LiveAudioMixer();
                StartAudioCapture();
            }

            _videoTask = Task.Run(VideoLoopAsync);
            if (_includeAudio)
            {
                _audioWriteTask = Task.Run(AudioWriteLoopAsync);
            }

            _clock.Start();
            _streamsGo.Set();
            IsRecording = true;
        }
        catch (OperationCanceledException)
        {
            _streamsGo.Set();
            await AbortStartAsync().ConfigureAwait(false);
            throw new TimeoutException("FFmpeg 启动超时。");
        }
        catch
        {
            _streamsGo.Set();
            await AbortStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<FfmpegSession> TakeOrCreateSessionAsync(
        string ffmpegPath,
        Size size,
        bool includeAudio,
        CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_sessionGate)
        {
            pending = _prepareTask;
        }

        if (pending != null)
        {
            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        lock (_sessionGate)
        {
            FfmpegSession? standby = _standby;
            if (standby != null && standby.IsAlive && standby.IsReady && standby.Matches(size, includeAudio))
            {
                _standby = null;
                return standby;
            }

            DiscardStandbyLocked();
        }

        return await FfmpegSession.StartAsync(ffmpegPath, size, includeAudio, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceStandbyAsync(string ffmpegPath, Size size, bool includeAudio, CancellationToken token)
    {
        FfmpegSession session = await FfmpegSession.StartAsync(ffmpegPath, size, includeAudio, token).ConfigureAwait(false);
        lock (_sessionGate)
        {
            if (token.IsCancellationRequested || _disposed || IsRecording)
            {
                session.Dispose();
                TryDeleteDirectory(session.TempDirectory);
                return;
            }

            DiscardStandbyLocked();
            _standby = session;
        }
    }

    private void DiscardStandbyLocked()
    {
        FfmpegSession? old = _standby;
        _standby = null;
        if (old == null)
        {
            return;
        }

        Task.Run(() =>
        {
            string temp = old.TempDirectory;
            old.Dispose();
            TryDeleteDirectory(temp);
        });
    }

    private void StartAudioCapture()
    {
        _audioAbandoned = false;
        _acceptAudio = true;
        _audioInitTask = Task.Run(InitializeAudio);
    }

    private void InitializeAudio()
    {
        if (_audioMode is AudioMode.System or AudioMode.Both)
        {
            try
            {
                WasapiLoopbackCapture capture = new();
                capture.DataAvailable += SystemCapture_DataAvailable;

                lock (_audioGate)
                {
                    if (_audioAbandoned || _stopRequested)
                    {
                        capture.Dispose();
                        return;
                    }

                    _systemCapture = capture;
                    _mixer?.AttachSystem(capture.WaveFormat);
                }

                capture.StartRecording();
            }
            catch
            {
                SetWarning("系统声音无法采集，已跳过。");
                DisposeSystemAudio();
            }
        }

        if (_audioMode is AudioMode.Microphone or AudioMode.Both)
        {
            try
            {
                WasapiCapture capture = new();
                capture.DataAvailable += MicrophoneCapture_DataAvailable;

                lock (_audioGate)
                {
                    if (_audioAbandoned || _stopRequested)
                    {
                        capture.Dispose();
                        return;
                    }

                    _microphoneCapture = capture;
                    _mixer?.AttachMicrophone(capture.WaveFormat);
                }

                capture.StartRecording();
            }
            catch
            {
                SetWarning("麦克风无法采集，已跳过（请检查 Windows 隐私设置中的麦克风权限）。");
                DisposeMicrophoneAudio();
            }
        }
    }

    private void SystemCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_acceptAudio || _paused || _stopRequested || e.BytesRecorded <= 0)
        {
            return;
        }

        _mixer?.WriteSystem(e.Buffer, e.BytesRecorded);
    }

    private void MicrophoneCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_acceptAudio || _paused || _stopRequested || e.BytesRecorded <= 0)
        {
            return;
        }

        _mixer?.WriteMicrophone(e.Buffer, e.BytesRecorded);
    }

    public void Pause()
    {
        if (!IsRecording || _paused)
        {
            return;
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

        _paused = false;
        _clock.Start();
    }

    private async Task VideoLoopAsync()
    {
        _streamsGo.Wait();
        FfmpegSession? session = _active;
        Stream? stream = session?.VideoStream;
        if (session == null || stream == null)
        {
            _captureError = "视频管道未就绪。";
            return;
        }

        try
        {
            int width = _region.Width;
            int height = _region.Height;
            int rowBytes = width * 4;
            byte[] buffer = new byte[rowBytes * height];

            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);

            long produced = 0;
            while (!_stopRequested)
            {
                if (_paused)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    continue;
                }

                CaptureFrame(bitmap, graphics);
                CopyBitmap(bitmap, buffer, width, height, rowBytes);

                long target = (long)(_clock.Elapsed.TotalMilliseconds * FramesPerSecond / 1000.0);
                if (target - produced > FramesPerSecond)
                {
                    produced = target;
                }

                while (produced < target && !_stopRequested)
                {
                    await WritePipeAsync(stream, buffer, produced == 0).ConfigureAwait(false);
                    produced++;
                }

                long nextDue = (produced + 1) * 1000 / FramesPerSecond;
                int wait = (int)(nextDue - _clock.Elapsed.TotalMilliseconds);
                if (wait > 2)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopRequested)
            {
                _captureError = "视频写入失败：" + ex.Message;
            }
        }
    }

    private static void CopyBitmap(Bitmap bitmap, byte[] buffer, int width, int height, int rowBytes)
    {
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                IntPtr top = data.Stride > 0
                    ? data.Scan0
                    : data.Scan0 + data.Stride * (height - 1);
                for (int y = 0; y < height; y++)
                {
                    IntPtr source = top + y * data.Stride;
                    Marshal.Copy(source, buffer, y * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void CaptureFrame(Bitmap bitmap, Graphics graphics)
    {
        bool captured = false;
        DxgiScreenCapture? dxgi = _dxgi;
        if (dxgi != null)
        {
            try
            {
                captured = dxgi.TryCapture(bitmap, _region);
                if (!dxgi.IsAvailable)
                {
                    _dxgi = null;
                    dxgi.Dispose();
                }
            }
            catch
            {
                captured = false;
            }
        }

        if (!captured)
        {
            try
            {
                graphics.CopyFromScreen(
                    _region.Left,
                    _region.Top,
                    0,
                    0,
                    new Size(_region.Width, _region.Height),
                    CopyPixelOperation.SourceCopy);
            }
            catch
            {
            }
        }

        DrawCursor(graphics, _region.Location);
    }

    private async Task AudioWriteLoopAsync()
    {
        FfmpegSession? session = _active;
        LiveAudioMixer? mixer = _mixer;
        if (session == null || mixer == null)
        {
            return;
        }

        Stream stream;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            stream = await session.WaitForAudioAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            SetWarning("声音管道未能接通，本次将保存为无声视频。");
            return;
        }

        _streamsGo.Wait();

        byte[] chunk = new byte[mixer.ChunkBytes];
        long produced = 0;
        try
        {
            while (!_stopRequested)
            {
                if (_paused)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    continue;
                }

                mixer.Read(chunk, 0, chunk.Length);
                await WritePipeAsync(stream, chunk, produced == 0).ConfigureAwait(false);
                produced++;

                long nextDue = produced * LiveAudioMixer.ChunkMilliseconds;
                int wait = (int)(nextDue - _clock.Elapsed.TotalMilliseconds);
                if (wait > 2)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task WritePipeAsync(Stream stream, byte[] buffer, bool firstWrite)
    {
        ValueTask write = stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
        if (!firstWrite)
        {
            await write.ConfigureAwait(false);
            return;
        }

        try
        {
            await write.AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("FFmpeg 首次写入超时。");
        }
    }

    private static void DrawCursor(Graphics graphics, Point origin)
    {
        NativeMethods.CURSORINFO cursor = new()
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>()
        };

        if (!NativeMethods.GetCursorInfo(ref cursor) ||
            cursor.flags != NativeMethods.CURSOR_SHOWING ||
            cursor.hCursor == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetIconInfo(cursor.hCursor, out NativeMethods.ICONINFO icon))
        {
            return;
        }

        try
        {
            int x = cursor.ptScreenPos.X - origin.X - icon.xHotspot;
            int y = cursor.ptScreenPos.Y - origin.Y - icon.yHotspot;
            IntPtr hdc = graphics.GetHdc();
            try
            {
                NativeMethods.DrawIconEx(
                    hdc,
                    x,
                    y,
                    cursor.hCursor,
                    0,
                    0,
                    0,
                    IntPtr.Zero,
                    NativeMethods.DI_NORMAL);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(icon.hbmColor);
            }
        }
    }

    public async Task<string?> StopAsync(bool discard)
    {
        lock (_stopGate)
        {
            if (!IsRecording)
            {
                return null;
            }

            IsRecording = false;
        }

        _clock.Stop();
        _stopRequested = true;
        _streamsGo.Set();

        await WaitQuietly(_videoTask, 4000).ConfigureAwait(false);
        await StopAudioCaptureAsync().ConfigureAwait(false);

        FfmpegSession? session = _active;
        _active = null;
        if (session != null)
        {
            await session.FinishAsync().ConfigureAwait(false);
        }

        string? path = null;
        try
        {
            if (!discard)
            {
                path = CollectOutput(session);
            }
        }
        finally
        {
            CleanupCapture();
            if (session != null)
            {
                string temp = session.TempDirectory;
                session.Dispose();
                if (discard)
                {
                    TryDeleteDirectory(temp);
                }
                else
                {
                    TryDeleteDirectory(temp);
                }
            }
        }

        SpawnStandby();
        return path;
    }

    private string CollectOutput(FfmpegSession? session)
    {
        string? videoError = _captureError;
        if (session == null || !File.Exists(session.OutputPath) || new FileInfo(session.OutputPath).Length == 0)
        {
            string log = session?.GetLog() ?? string.Empty;
            throw new InvalidOperationException(
                "录像没有成功生成。" +
                (videoError != null ? "\n\n" + videoError : string.Empty) +
                (string.IsNullOrWhiteSpace(log) ? string.Empty : "\n\nFFmpeg 信息：\n" + log));
        }

        Directory.CreateDirectory(_outputFolder);
        string finalPath = GetPlannedOutputPath();
        File.Move(session.OutputPath, finalPath, true);
        return finalPath;
    }

    private void SpawnStandby()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return;
        }

        Rectangle region = _region.Width >= 16 ? _region : PrimaryScreenRegion();
        _ = PrepareAsync(_ffmpegPath, region, _audioMode);
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

    private async Task StopAudioCaptureAsync()
    {
        _acceptAudio = false;
        _audioAbandoned = true;
        await WaitQuietly(_audioInitTask, 3000).ConfigureAwait(false);
        _audioInitTask = null;

        try { _systemCapture?.StopRecording(); } catch { }
        try { _microphoneCapture?.StopRecording(); } catch { }

        await WaitQuietly(_audioWriteTask, 3000).ConfigureAwait(false);
        _audioWriteTask = null;

        try { _systemCapture?.Dispose(); } catch { }
        try { _microphoneCapture?.Dispose(); } catch { }
        _systemCapture = null;
        _microphoneCapture = null;
    }

    private async Task AbortStartAsync()
    {
        _stopRequested = true;
        _streamsGo.Set();
        await WaitQuietly(_videoTask, 2000).ConfigureAwait(false);
        await StopAudioCaptureAsync().ConfigureAwait(false);
        FfmpegSession? session = _active;
        _active = null;
        CleanupCapture();
        if (session != null)
        {
            string temp = session.TempDirectory;
            session.Dispose();
            TryDeleteDirectory(temp);
        }
    }

    private void CleanupCapture()
    {
        try { _mixer?.Dispose(); } catch { }
        _mixer = null;
        try { _dxgi?.Dispose(); } catch { }
        _dxgi = null;
        _paused = false;
        _clock.Reset();
        _videoTask = null;
        _audioWriteTask = null;
    }

    private void DisposeSystemAudio()
    {
        lock (_audioGate)
        {
            try { _systemCapture?.StopRecording(); } catch { }
            _systemCapture?.Dispose();
            _systemCapture = null;
        }
    }

    private void DisposeMicrophoneAudio()
    {
        lock (_audioGate)
        {
            try { _microphoneCapture?.StopRecording(); } catch { }
            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
        }
    }

    private void SetWarning(string message)
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

    private static async Task WaitQuietly(Task? task, int milliseconds)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
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
        _stopRequested = true;
        _streamsGo.Set();
        _prepareCts?.Cancel();
        try { _prepareTask?.Wait(2000); } catch { }
        lock (_sessionGate)
        {
            DiscardStandbyLocked();
        }

        FfmpegSession? active = _active;
        _active = null;
        active?.Dispose();
        CleanupCapture();
        try { _systemCapture?.Dispose(); } catch { }
        try { _microphoneCapture?.Dispose(); } catch { }
        _streamsGo.Dispose();
        _prepareCts?.Dispose();
    }
}
