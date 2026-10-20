using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EasyRecorder;

public sealed class Recorder : IDisposable
{
    private const int FramesPerSecond = 30;

    private readonly object _audioGate = new();
    private readonly object _logGate = new();
    private readonly object _stopGate = new();
    private readonly Stopwatch _clock = new();

    private Process? _ffmpeg;
    private Thread? _videoThread;
    private Thread? _audioInitThread;
    private Thread? _audioWriteThread;
    private NamedPipeServerStream? _audioPipe;
    private LiveAudioMixer? _mixer;
    private DxgiScreenCapture? _dxgi;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private volatile bool _acceptAudio;
    private volatile bool _audioAbandoned;
    private Rectangle _region;
    private string _tempDirectory = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputFolder = string.Empty;
    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _microphoneCapture;
    private readonly StringBuilder _ffmpegLog = new();
    private string? _captureError;
    private AudioMode _audioMode;
    private DateTime _startedAt;
    private bool _disposed;
    private bool _includeAudio;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? Warning { get; private set; }

    public event Action<string>? WarningRaised;

    public async Task StartAsync(Rectangle region, string ffmpegPath, string outputFolder, AudioMode audioMode)
    {
        if (IsRecording)
        {
            return;
        }

        await Task.Run(() => StartCore(region, ffmpegPath, outputFolder, audioMode));
    }

    private void StartCore(Rectangle region, string ffmpegPath, string outputFolder, AudioMode audioMode)
    {
        Rectangle screen = SystemInformation.VirtualScreen;
        region.Intersect(screen);
        region.Width -= region.Width % 2;
        region.Height -= region.Height % 2;

        if (region.Width < 16 || region.Height < 16)
        {
            throw new InvalidOperationException("录制区域太小，请重新框选。");
        }

        Directory.CreateDirectory(outputFolder);
        _region = region;
        _audioMode = audioMode;
        _outputFolder = outputFolder;
        _startedAt = DateTime.Now;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "EasyRecorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _outputPath = Path.Combine(_tempDirectory, "recording.mp4");
        _captureError = null;
        Warning = null;
        _stopRequested = false;
        _paused = false;
        _acceptAudio = false;
        _includeAudio = audioMode != AudioMode.None;
        _clock.Reset();
        lock (_logGate)
        {
            _ffmpegLog.Clear();
        }

        try
        {
            _dxgi = DxgiScreenCapture.TryCreate(_region);
            string? audioPipePath = null;
            if (_includeAudio)
            {
                _mixer = new LiveAudioMixer();
                audioPipePath = CreateAudioPipe();
                StartAudioCapture();
                _audioWriteThread = new Thread(AudioWriteLoop)
                {
                    IsBackground = true,
                    Name = "EasyRecorder.AudioWrite"
                };
                _audioWriteThread.Start();
            }

            StartVideoProcess(ffmpegPath, audioPipePath);
            _videoThread = new Thread(VideoLoop)
            {
                IsBackground = true,
                Name = "EasyRecorder.Video"
            };
            _videoThread.Start();

            _clock.Start();
            IsRecording = true;
        }
        catch
        {
            CleanupProcess();
            CleanupAudio();
            _dxgi?.Dispose();
            _dxgi = null;
            TryDeleteDirectory(_tempDirectory);
            throw;
        }
    }

    private string CreateAudioPipe()
    {
        string pipeName = "EasyRecorderAudio_" + Guid.NewGuid().ToString("N");
        _audioPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536,
            65536);
        return @"\\.\pipe\" + pipeName;
    }

    private void StartVideoProcess(string ffmpegPath, string? audioPipePath)
    {
        ProcessStartInfo info = new(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-thread_queue_size");
        info.ArgumentList.Add("512");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("rawvideo");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("bgra");
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add($"{_region.Width}x{_region.Height}");
        info.ArgumentList.Add("-r");
        info.ArgumentList.Add(FramesPerSecond.ToString());
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add("pipe:0");

        if (audioPipePath != null)
        {
            info.ArgumentList.Add("-thread_queue_size");
            info.ArgumentList.Add("512");
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("s16le");
            info.ArgumentList.Add("-ar");
            info.ArgumentList.Add(LiveAudioMixer.SampleRate.ToString());
            info.ArgumentList.Add("-ac");
            info.ArgumentList.Add(LiveAudioMixer.Channels.ToString());
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(audioPipePath);
        }

        info.ArgumentList.Add("-c:v");
        info.ArgumentList.Add("libx264");
        info.ArgumentList.Add("-preset");
        info.ArgumentList.Add("ultrafast");
        info.ArgumentList.Add("-crf");
        info.ArgumentList.Add("20");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("yuv420p");
        if (_includeAudio)
        {
            info.ArgumentList.Add("-c:a");
            info.ArgumentList.Add("aac");
            info.ArgumentList.Add("-b:a");
            info.ArgumentList.Add("192k");
            info.ArgumentList.Add("-ar");
            info.ArgumentList.Add("48000");
            info.ArgumentList.Add("-ac");
            info.ArgumentList.Add("2");
        }
        else
        {
            info.ArgumentList.Add("-an");
        }

        info.ArgumentList.Add("-movflags");
        info.ArgumentList.Add("+faststart");
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add(_outputPath);

        _ffmpeg = new Process { StartInfo = info };
        _ffmpeg.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            lock (_logGate)
            {
                if (_ffmpegLog.Length < 30000)
                {
                    _ffmpegLog.AppendLine(args.Data);
                }
            }
        };

        if (!_ffmpeg.Start())
        {
            throw new InvalidOperationException("无法启动 FFmpeg。");
        }

        _ffmpeg.BeginErrorReadLine();
    }

    private void StartAudioCapture()
    {
        _audioAbandoned = false;
        _acceptAudio = true;
        _audioInitThread = new Thread(InitializeAudio)
        {
            IsBackground = true,
            Name = "EasyRecorder.AudioInit"
        };
        _audioInitThread.Start();
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

    private void VideoLoop()
    {
        try
        {
            int width = _region.Width;
            int height = _region.Height;
            int rowBytes = width * 4;
            byte[] buffer = new byte[rowBytes * height];

            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            Stream? stdin = null;

            long produced = 0;
            while (!_stopRequested)
            {
                if (_paused)
                {
                    Thread.Sleep(10);
                    continue;
                }

                CaptureFrame(bitmap, graphics);

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

                long target = (long)(_clock.Elapsed.TotalMilliseconds * FramesPerSecond / 1000.0);
                if (target - produced > FramesPerSecond)
                {
                    produced = target;
                }

                try
                {
                    stdin ??= _ffmpeg!.StandardInput.BaseStream;
                    while (produced < target && !_stopRequested)
                    {
                        stdin.Write(buffer, 0, buffer.Length);
                        produced++;
                    }
                }
                catch (Exception ex)
                {
                    _captureError = "视频写入失败：" + ex.Message;
                    break;
                }

                long nextDue = (produced + 1) * 1000 / FramesPerSecond;
                int wait = (int)(nextDue - _clock.Elapsed.TotalMilliseconds);
                if (wait > 2)
                {
                    Thread.Sleep(wait);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            _captureError = "屏幕采集失败：" + ex.Message;
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

    private void AudioWriteLoop()
    {
        NamedPipeServerStream? pipe = _audioPipe;
        LiveAudioMixer? mixer = _mixer;
        if (pipe == null || mixer == null)
        {
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            SetWarning("声音管道未能接通，本次将保存为无声视频。");
            return;
        }

        byte[] chunk = new byte[mixer.ChunkBytes];
        long produced = 0;
        try
        {
            while (!_stopRequested)
            {
                if (_paused)
                {
                    Thread.Sleep(10);
                    continue;
                }

                mixer.Read(chunk, 0, chunk.Length);
                pipe.Write(chunk, 0, chunk.Length);
                produced++;

                long nextDue = produced * LiveAudioMixer.ChunkMilliseconds;
                int wait = (int)(nextDue - _clock.Elapsed.TotalMilliseconds);
                if (wait > 2)
                {
                    Thread.Sleep(wait);
                }
            }

            try
            {
                pipe.Flush();
            }
            catch
            {
            }
        }
        catch
        {
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

        Thread? videoThread = _videoThread;
        if (videoThread != null)
        {
            videoThread.Join(4000);
        }

        StopAudioCapture();
        ClosePipes();

        await Task.Run(CloseVideoProcess);

        if (discard)
        {
            Cleanup();
            return null;
        }

        string? videoError = _captureError;
        if (!File.Exists(_outputPath) || new FileInfo(_outputPath).Length == 0)
        {
            string log = GetFfmpegLog();
            Cleanup();
            throw new InvalidOperationException(
                "录像没有成功生成。" +
                (videoError != null ? "\n\n" + videoError : string.Empty) +
                (string.IsNullOrWhiteSpace(log) ? string.Empty : "\n\nFFmpeg 信息：\n" + log));
        }

        Directory.CreateDirectory(_outputFolder);
        string finalPath = GetPlannedOutputPath();
        File.Move(_outputPath, finalPath, true);
        Cleanup();
        return finalPath;
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

    private void StopAudioCapture()
    {
        _acceptAudio = false;
        _audioAbandoned = true;

        Thread? initThread = _audioInitThread;
        if (initThread != null && initThread.IsAlive)
        {
            initThread.Join(3000);
        }

        _audioInitThread = null;

        try
        {
            _systemCapture?.StopRecording();
        }
        catch
        {
        }

        try
        {
            _microphoneCapture?.StopRecording();
        }
        catch
        {
        }

        Thread? writeThread = _audioWriteThread;
        if (writeThread != null && writeThread.IsAlive)
        {
            writeThread.Join(3000);
        }

        _audioWriteThread = null;

        try
        {
            _systemCapture?.Dispose();
        }
        catch
        {
        }

        try
        {
            _microphoneCapture?.Dispose();
        }
        catch
        {
        }

        _systemCapture = null;
        _microphoneCapture = null;
    }

    private void ClosePipes()
    {
        try
        {
            _ffmpeg?.StandardInput.BaseStream.Flush();
            _ffmpeg?.StandardInput.Close();
        }
        catch
        {
        }

        try
        {
            _audioPipe?.Dispose();
        }
        catch
        {
        }

        _audioPipe = null;
    }

    private void CloseVideoProcess()
    {
        if (_ffmpeg == null)
        {
            return;
        }

        try
        {
            if (!_ffmpeg.WaitForExit(20000))
            {
                _ffmpeg.Kill(true);
                _ffmpeg.WaitForExit(3000);
            }
            else
            {
                _ffmpeg.WaitForExit();
            }
        }
        catch
        {
        }
    }

    private string GetFfmpegLog()
    {
        lock (_logGate)
        {
            return _ffmpegLog.ToString().Trim();
        }
    }

    private void CleanupProcess()
    {
        try
        {
            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                _ffmpeg.Kill(true);
            }
        }
        catch
        {
        }

        try
        {
            _ffmpeg?.Dispose();
        }
        catch
        {
        }

        _ffmpeg = null;
        try
        {
            _audioPipe?.Dispose();
        }
        catch
        {
        }

        _audioPipe = null;
    }

    private void DisposeSystemAudio()
    {
        lock (_audioGate)
        {
            try
            {
                _systemCapture?.StopRecording();
            }
            catch
            {
            }

            _systemCapture?.Dispose();
            _systemCapture = null;
        }
    }

    private void DisposeMicrophoneAudio()
    {
        lock (_audioGate)
        {
            try
            {
                _microphoneCapture?.StopRecording();
            }
            catch
            {
            }

            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
        }
    }

    private void CleanupAudio()
    {
        StopAudioCapture();
        try
        {
            _mixer?.Dispose();
        }
        catch
        {
        }

        _mixer = null;
    }

    private void Cleanup()
    {
        CleanupProcess();
        CleanupAudio();
        try
        {
            _dxgi?.Dispose();
        }
        catch
        {
        }

        _dxgi = null;
        IsRecording = false;
        _paused = false;
        _clock.Reset();
        TryDeleteDirectory(_tempDirectory);
    }

    private void SetWarning(string message)
    {
        Warning = AppendWarning(Warning, message);
        try
        {
            WarningRaised?.Invoke(message);
        }
        catch
        {
        }
    }

    private static string AppendWarning(string? current, string message)
    {
        return string.IsNullOrWhiteSpace(current) ? message : current + "\n" + message;
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
        Cleanup();
    }
}
