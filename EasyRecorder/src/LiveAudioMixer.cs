using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyRecorder;

internal sealed class LiveAudioMixer : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int ChunkMilliseconds = 20;

    public WaveFormat OutputFormat { get; } = new WaveFormat(SampleRate, BitsPerSample, Channels);
    public int ChunkBytes { get; } = SampleRate * Channels * (BitsPerSample / 8) * ChunkMilliseconds / 1000;

    private readonly object _gate = new();
    private BufferedWaveProvider? _systemBuffer;
    private BufferedWaveProvider? _micBuffer;
    private SampleToWaveProvider16? _output;
    private bool _disposed;

    public bool HasSource
    {
        get
        {
            lock (_gate)
            {
                return _systemBuffer != null || _micBuffer != null;
            }
        }
    }

    public void AttachSystem(WaveFormat format)
    {
        lock (_gate)
        {
            _systemBuffer = CreateBuffer(format);
            Rebuild();
        }
    }

    public void AttachMicrophone(WaveFormat format)
    {
        lock (_gate)
        {
            _micBuffer = CreateBuffer(format);
            Rebuild();
        }
    }

    public void WriteSystem(byte[] buffer, int count)
    {
        try
        {
            _systemBuffer?.AddSamples(buffer, 0, count);
        }
        catch
        {
        }
    }

    public void WriteMicrophone(byte[] buffer, int count)
    {
        try
        {
            _micBuffer?.AddSamples(buffer, 0, count);
        }
        catch
        {
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        SampleToWaveProvider16? output;
        lock (_gate)
        {
            output = _output;
        }

        if (output == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int read = output.Read(buffer, offset, count);
        if (read < count)
        {
            Array.Clear(buffer, offset + read, count - read);
        }

        return count;
    }

    private void Rebuild()
    {
        MixingSampleProvider mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true
        };

        if (_systemBuffer != null)
        {
            mixer.AddMixerInput(Convert(_systemBuffer));
        }

        if (_micBuffer != null)
        {
            mixer.AddMixerInput(Convert(_micBuffer));
        }

        _output = new SampleToWaveProvider16(mixer);
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format)
    {
        return new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = Math.Max(format.AverageBytesPerSecond * 2, 16384)
        };
    }

    private static ISampleProvider Convert(IWaveProvider source)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        if (samples.WaveFormat.Channels == 1)
        {
            samples = new MonoToStereoSampleProvider(samples);
        }
        else if (samples.WaveFormat.Channels > 2)
        {
            samples = new MultiplexingSampleProvider(new[] { samples }, 2);
        }

        return samples;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _systemBuffer = null;
            _micBuffer = null;
            _output = null;
        }
    }
}
