using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace EasyRecorder;

internal sealed class FfmpegSession : IDisposable
{
    private readonly object _logGate = new();
    private readonly StringBuilder _log = new();
    private Process? _process;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private bool _disposed;

    public Size FrameSize { get; }
    public bool IncludeAudio { get; }
    public string OutputPath { get; }
    public string TempDirectory { get; }
    public Stream? VideoStream => _videoPipe;
    public Stream? AudioStream => _audioPipe;

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
        string videoName = "EasyRecorderVideo_" + Guid.NewGuid().ToString("N");
        _videoPipe = CreatePipe(videoName);

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
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8
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
        info.ArgumentList.Add(@"\\.\pipe\" + videoName);

        if (audioName != null)
        {
            info.ArgumentList.Add("-probesize");
            info.ArgumentList.Add("32");
            info.ArgumentList.Add("-analyzeduration");
            info.ArgumentList.Add("0");
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
        _process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            lock (_logGate)
            {
                if (_log.Length < 30000)
                {
                    _log.AppendLine(args.Data);
                }
            }
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 FFmpeg。");
        }

        _process.BeginErrorReadLine();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await _videoPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        if (_audioPipe != null)
        {
            await _audioPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }

        if (!IsAlive)
        {
            throw new InvalidOperationException("FFmpeg 在待命时退出。" + FormatLog());
        }

        IsReady = true;
    }

    public async Task FinishAsync()
    {
        IsReady = false;
        await ClosePipeAsync(_videoPipe).ConfigureAwait(false);
        await ClosePipeAsync(_audioPipe).ConfigureAwait(false);
        _videoPipe = null;
        _audioPipe = null;

        Process? process = _process;
        if (process == null)
        {
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    public string GetLog()
    {
        lock (_logGate)
        {
            return _log.ToString().Trim();
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

    private static async Task ClosePipeAsync(NamedPipeServerStream? pipe)
    {
        if (pipe == null)
        {
            return;
        }

        try
        {
            if (pipe.IsConnected)
            {
                await pipe.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            pipe.Dispose();
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

        try
        {
            _process?.Dispose();
        }
        catch
        {
        }

        _process = null;

        try
        {
            _videoPipe?.Dispose();
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

        _videoPipe = null;
        _audioPipe = null;
    }
}
