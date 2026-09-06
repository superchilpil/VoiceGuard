using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceGuard;

internal static class ReplacementSoundImporter
{
    public const double MaxDurationSeconds = 5.0;
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 1;

    public static string GetStorageDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceGuard", "ReplacementSounds");
    }

    public static string Import(string sourcePath, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("No audio file was selected.", nameof(sourcePath));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected audio file could not be found.", sourcePath);

        Directory.CreateDirectory(GetStorageDirectory());

        string destination = BuildDestinationPath(sourcePath);
        string displayName = Path.GetFileName(sourcePath);
        log($"CONVERTING: {displayName}");

        string tempPath = destination + ".tmp";
        try
        {
            using var reader = new MediaFoundationReader(sourcePath);
            double duration = reader.TotalTime.TotalSeconds;

            if (duration > MaxDurationSeconds)
                log($"TRIMMING: replacement sound limited to {MaxDurationSeconds:0.0} seconds");

            ISampleProvider samples = reader.ToSampleProvider();

            if (samples.WaveFormat.Channels > OutputChannels)
                samples = samples.ToMono();
            else if (samples.WaveFormat.Channels == 0)
                throw new InvalidDataException("The audio file has no audio channels.");

            if (samples.WaveFormat.SampleRate != OutputSampleRate)
                samples = new WdlResamplingSampleProvider(samples, OutputSampleRate);

            samples = new MaxDurationSampleProvider(samples, MaxDurationSeconds);
            WaveFileWriter.CreateWaveFile16(tempPath, samples);

            File.Move(tempPath, destination, true);
            log($"READY: replacement sound stored as {Path.GetFileName(destination)}");
            return destination;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static string BuildDestinationPath(string sourcePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "replacement";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(baseName.Length);
        foreach (char c in baseName)
            safe.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        string fingerprintSource;
        try
        {
            var info = new FileInfo(sourcePath);
            fingerprintSource = $"{Path.GetFullPath(sourcePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            fingerprintSource = Path.GetFullPath(sourcePath);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource));
        string suffix = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return Path.Combine(GetStorageDirectory(), $"{safe}_{suffix}.wav");
    }

    private sealed class MaxDurationSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly long maxSamples;
        private long samplesRead;

        public MaxDurationSampleProvider(ISampleProvider source, double maxSeconds)
        {
            this.source = source;
            maxSamples = (long)Math.Floor(source.WaveFormat.SampleRate * maxSeconds) * source.WaveFormat.Channels;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            long remaining = maxSamples - samplesRead;
            if (remaining <= 0)
                return 0;

            int allowed = (int)Math.Min(count, remaining);
            int read = source.Read(buffer, offset, allowed);
            samplesRead += read;
            return read;
        }
    }
}
