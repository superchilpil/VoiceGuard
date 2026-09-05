using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;

namespace VoiceGuard;

public sealed class AudioEngine : IDisposable
{
    private readonly object sync = new();
    private readonly BufferedWaveProvider inputBuffer;
    private readonly WaveInEvent input;
    private readonly WaveOutEvent output;
    private readonly MixingSampleProvider mixer;
    private readonly WaveFormat format;
    private readonly ConcurrentQueue<CensorRegion> regions = new();
    private bool disposed;

    public AudioEngine(int deviceNumber, int outputDeviceNumber, int sampleRate = 16000, int channels = 1)
    {
        format = new WaveFormat(sampleRate, 16, channels);
        inputBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
        input = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = format, BufferMilliseconds = 100 };
        input.DataAvailable += Input_DataAvailable;
        mixer = new MixingSampleProvider(format.ToSampleProvider()) { ReadFully = true };
        output = new WaveOutEvent { DeviceNumber = outputDeviceNumber, DesiredLatency = 100 };
        output.Init(mixer);
    }

    public event EventHandler<byte[]>? AudioCaptured;
    public WaveFormat WaveFormat => format;

    public void Start()
    {
        ThrowIfDisposed();
        input.StartRecording();
        output.Play();
    }

    public void Stop()
    {
        if (disposed) return;
        input.StopRecording();
        output.Stop();
    }

    private void Input_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (disposed) return;
        var bytes = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, bytes, 0, e.BytesRecorded);
        inputBuffer.AddSamples(bytes, 0, bytes.Length);
        AudioCaptured?.Invoke(this, bytes);
    }

    public void AddCensorRegion(double startSeconds, double durationSeconds, byte[]? replacementPcm = null)
    {
        var preRoll = 0.06;
        var postRoll = 0.30;
        var start = Math.Max(0, startSeconds - preRoll);
        var end = startSeconds + durationSeconds + postRoll;
        regions.Enqueue(new CensorRegion(start, end, replacementPcm, startSeconds));
    }

    public IReadOnlyList<CensorRegion> SnapshotRegions() => regions.ToArray();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        input.DataAvailable -= Input_DataAvailable;
        input.Dispose();
        output.Dispose();
        inputBuffer.ClearBuffer();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(AudioEngine));
    }

    public sealed record CensorRegion(double StartSeconds, double EndSeconds, byte[]? ReplacementPcm, double EffectStartSeconds);
}
