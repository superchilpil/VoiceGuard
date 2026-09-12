using System;
using System.IO;
using NAudio.Wave;

namespace VoiceGuard;

public static class SoundboardImporter
{
    public static string ExportSelection(string sourcePath, double startSeconds, double endSeconds, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Soundboard source audio was not found.", sourcePath);
        if (endSeconds <= startSeconds)
            throw new ArgumentException("The soundboard selection must have a positive duration.");

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceGuard", "Soundboard");
        Directory.CreateDirectory(root);

        string safeName = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        string outputPath = Path.Combine(root, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmssfff}.wav");

        using var reader = new MediaFoundationReader(sourcePath);
        double duration = reader.TotalTime.TotalSeconds;
        startSeconds = Math.Clamp(startSeconds, 0, duration);
        endSeconds = Math.Clamp(endSeconds, startSeconds, duration);
        if (endSeconds <= startSeconds + 0.001)
            throw new ArgumentException("The selected audio section is empty.");

        reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);
        var outputFormat = new WaveFormat(48000, 16, 1);
        using var writer = new WaveFileWriter(outputPath, outputFormat);
        int bytesPerFrame = reader.WaveFormat.BitsPerSample / 8 * reader.WaveFormat.Channels;
        if (bytesPerFrame <= 0) throw new InvalidDataException("Unsupported source audio format.");

        int blockFrames = Math.Max(256, reader.WaveFormat.SampleRate / 10);
        int blockBytes = blockFrames * bytesPerFrame;
        byte[] raw = new byte[blockBytes];
        double sourcePos = startSeconds;
        var mono = new float[blockFrames];

        while (sourcePos < endSeconds - 0.000001)
        {
            int maxBytes = (int)Math.Min(raw.Length, Math.Ceiling((endSeconds - sourcePos) * reader.WaveFormat.AverageBytesPerSecond));
            maxBytes -= maxBytes % bytesPerFrame;
            if (maxBytes <= 0) break;
            int read = reader.Read(raw, 0, maxBytes);
            if (read <= 0) break;
            int frames = read / bytesPerFrame;
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                int off = f * bytesPerFrame;
                for (int ch = 0; ch < reader.WaveFormat.Channels; ch++)
                    sum += Decode(raw, off + ch * (reader.WaveFormat.BitsPerSample / 8), reader.WaveFormat.BitsPerSample, reader.WaveFormat.Encoding);
                mono[f] = (float)Math.Clamp(sum / reader.WaveFormat.Channels, -1, 1);
            }

            int outputFrames = Math.Max(1, (int)Math.Round(frames * (48000.0 / reader.WaveFormat.SampleRate)));
            for (int i = 0; i < outputFrames; i++)
            {
                double src = i * (reader.WaveFormat.SampleRate / 48000.0);
                int i0 = Math.Clamp((int)Math.Floor(src), 0, frames - 1);
                int i1 = Math.Min(i0 + 1, frames - 1);
                float frac = (float)(src - Math.Floor(src));
                float sample = mono[i0] + (mono[i1] - mono[i0]) * frac;
                short s = (short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue);
                writer.WriteByte((byte)(s & 0xFF));
                writer.WriteByte((byte)((s >> 8) & 0xFF));
            }
            sourcePos += frames / (double)reader.WaveFormat.SampleRate;
        }

        writer.Flush();
        log?.Invoke($"SOUNDBOARD CLIP SAVED — {Path.GetFileName(outputPath)} | {endSeconds - startSeconds:0.000}s");
        return outputPath;
    }

    private static float Decode(byte[] raw, int offset, int bits, WaveFormatEncoding encoding)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat)
        {
            if (bits == 32) return BitConverter.ToSingle(raw, offset);
            if (bits == 64) return (float)BitConverter.ToDouble(raw, offset);
        }
        if (bits == 8) return (raw[offset] - 128) / 128f;
        if (bits == 16) return BitConverter.ToInt16(raw, offset) / 32768f;
        if (bits == 24)
        {
            int value = raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            return value / 8388608f;
        }
        if (bits == 32) return BitConverter.ToInt32(raw, offset) / 2147483648f;
        throw new InvalidDataException($"Unsupported audio bit depth: {bits}");
    }
}
