using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace EasyRecorder;

internal sealed class FfmpegSession : IDisposable
{
    private const int StartTimeoutMs = 5000;
    private const int ExitTimeoutMs = 5000;

    private readonly object _logGate = new();
    private readonly StringBuilder _log = new();
    private Process? _process;
    private NamedPipeServerStream? _audioPipe;
    private Stream? _videoStream;
    private bool _disposed;

    public Size FrameSize { get; }
    public bool IncludeAudio { get; }
    public string OutputPath { get; }
    public string TempDirectory { get; }
    public Stream? VideoStream => _videoStream;
    public NamedPipeServerStream? AudioPipe => _audioPipe;

    public bool IsAlive
    {
        get
        {
            try
            {
                return _process != null && !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsReady { get; private set; }

    private FfmpegSession(Size frameSize, bool includeAudio, string outputPath, string tempDirectory)
    {
        FrameSize = frameSize;
        IncludeAudio = includeAudio;
        OutputPath = outputPath;
        TempDirectory = tempDirectory;
    }

    public bool Matches(Size frameSize, bool includeAudio)
    {
        return FrameSize == frameSize && IncludeAudio == includeAudio;
    }

    public static async Task<FfmpegSession> StartAsync(
        string ffmpegPath,
        Size frameSize,
        bool includeAudio,
        CancellationToken cancellationToken)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "EasyRecorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string outputPath = Path.Combine(tempDirectory, "recording.mp4");
        FfmpegSession session = new(frameSize, includeAudio, outputPath, tempDirectory);

        try
        {
            await session.LaunchAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task LaunchAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        string? audioName = null;
        if (IncludeAudio)
        {
            audioName = "EasyRecorderAudio_" + Guid.NewGuid().ToString("N");
            _audioPipe = CreatePipe(audioName);
        }

        ProcessStartInfo info = new(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-fflags");
        info.ArgumentList.Add("+nobuffer");
        info.ArgumentList.Add("-probesize");
        info.ArgumentList.Add("32");
        info.ArgumentList.Add("-analyzeduration");
        info.ArgumentList.Add("0");
        info.ArgumentList.Add("-thread_queue_size");
        info.ArgumentList.Add("512");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("rawvideo");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("bgra");
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add($"{FrameSize.Width}x{FrameSize.Height}");
        info.ArgumentList.Add("-r");
        info.ArgumentList.Add("30");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add("pipe:0");

        if (audioName != null)
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
            info.ArgumentList.Add(@"\\.\pipe\" + audioName);
        }

        info.ArgumentList.Add("-c:v");
        info.ArgumentList.Add("libx264");
        info.ArgumentList.Add("-preset");
        info.ArgumentList.Add("ultrafast");
        info.ArgumentList.Add("-crf");
        info.ArgumentList.Add("20");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("yuv420p");
        if (IncludeAudio)
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
        info.ArgumentList.Add(OutputPath);

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => AppendLog(args.Data);
        _process.ErrorDataReceived += (_, args) => AppendLog(args.Data);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeoutMs);

        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 FFmpeg。");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _videoStream = _process.StandardInput.BaseStream;

        try
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw new TimeoutException("FFmpeg 启动超时。");
        }

        if (!IsAlive)
        {
            throw new InvalidOperationException("FFmpeg 在待命时退出。" + FormatLog());
        }

        IsReady = true;
    }

    public async Task<Stream> WaitForAudioAsync(CancellationToken cancellationToken)
    {
        if (_audioPipe == null)
        {
            throw new InvalidOperationException("没有音频管道。");
        }

        if (_audioPipe.IsConnected)
        {
            return _audioPipe;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeoutMs);
        try
        {
            await _audioPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("音频管道连接超时。");
        }

        return _audioPipe;
    }

    public async Task FinishAsync()
    {
        IsReady = false;

        try
        {
            _videoStream?.Flush();
            _process?.StandardInput.Close();
        }
        catch
        {
        }

        _videoStream = null;

        try
        {
            if (_audioPipe != null)
            {
                if (_audioPipe.IsConnected)
                {
                    await _audioPipe.FlushAsync().ConfigureAwait(false);
                }

                _audioPipe.Dispose();
            }
        }
        catch
        {
        }

        _audioPipe = null;

        Process? process = _process;
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.WaitForExit(ExitTimeoutMs))
            {
                KillProcess();
                process.WaitForExit(1000);
            }
        }
        catch
        {
            KillProcess();
        }
    }

    public string GetLog()
    {
        lock (_logGate)
        {
            return _log.ToString().Trim();
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_logGate)
        {
            if (_log.Length < 30000)
            {
                _log.AppendLine(line);
            }
        }
    }

    private string FormatLog()
    {
        string log = GetLog();
        return string.IsNullOrWhiteSpace(log) ? string.Empty : "\n\nFFmpeg 信息：\n" + log;
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        return new NamedPipeServerStream(
            name,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            1024 * 1024,
            1024 * 1024);
    }

    private void KillProcess()
    {
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(true);
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
        IsReady = false;
        KillProcess();

        try
        {
            _process?.Dispose();
        }
        catch
        {
        }

        _process = null;
        _videoStream = null;

        try
        {
            _audioPipe?.Dispose();
        }
        catch
        {
        }

        _audioPipe = null;
    }
}
