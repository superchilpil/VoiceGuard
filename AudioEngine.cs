using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace VoiceGuard;

public sealed class AudioEngine : IDisposable
{
    private readonly int inputDevice;
    private readonly int outputDevice;
    private readonly double delaySeconds;
    private readonly Action<string> status;
    private readonly Action<string> log;
    private readonly Action<byte[], int, double> analysisAudio;
    private readonly Action<double>? analysisSegmentStart;
    private readonly Action<double>? analysisSegmentEnd;
    private readonly Func<double>? analysisCompletedSeconds;
    private readonly Func<bool>? analysisHasPending;
    private readonly Func<double>? analysisSafeThroughSeconds;
    private readonly Func<string, string?>? replacementSoundResolver;

    private WaveInEvent? capture;
    private WaveOutEvent? output;
    private BufferedWaveProvider? delayed;
    private SwitchProvider? switcher;

    private readonly object stateLock = new();
    private readonly object censorLock = new();
    private readonly List<CensorRegion> censorRegions = new();
    private double delayedReadSeconds;
    private bool ptt;
    private bool delayedMode;
    private bool stopped;
    private long capturePackets;
    private long captureBytes;
    private double capturePcmSeconds;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int Bits = 16;
    private const int BytesPerSecond = SampleRate * Channels * Bits / 8;

    public AudioEngine(
        int inputDevice, int outputDevice, double delaySeconds, Action<string> status,
        Action<byte[], int, double> analysisAudio, Action<string>? logCallback = null,
        Action<double>? analysisSegmentStart = null, Action<double>? analysisSegmentEnd = null,
        Func<double>? analysisCompletedSeconds = null, Func<bool>? analysisHasPending = null,
        Func<double>? analysisSafeThroughSeconds = null, Func<string, string?>? replacementSoundResolver = null)
    {
        this.inputDevice = inputDevice; this.outputDevice = outputDevice; this.delaySeconds = delaySeconds;
        this.status = status; this.analysisAudio = analysisAudio;
        this.analysisSegmentStart = analysisSegmentStart; this.analysisSegmentEnd = analysisSegmentEnd;
        this.analysisCompletedSeconds = analysisCompletedSeconds; this.analysisHasPending = analysisHasPending;
        this.analysisSafeThroughSeconds = analysisSafeThroughSeconds;
        this.replacementSoundResolver = replacementSoundResolver;
        this.log = logCallback ?? (_ => { });
    }

    public double CurrentSourceSeconds => switcher?.CurrentSourceSeconds ?? delayedReadSeconds;

    public void Start()
    {
        var format = new WaveFormat(SampleRate, Bits, Channels);
        delayed = new BufferedWaveProvider(format)
        {
            BufferLength = BytesPerSecond * 15,
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };

        // Delayed mode starts immediately. The output timeline therefore always
        // represents the real capture clock, even before the first PTT press.
        // Non-PTT packets are written as silence, never as microphone PCM.
        delayedMode = true;

        switcher = new SwitchProvider(
            format, delayed, GetState, () => delaySeconds, () => capturePcmSeconds,
            IsCensored, GetCensorRegions, GetCensorRegion, status, log);

        output = new WaveOutEvent { DeviceNumber = outputDevice, DesiredLatency = 80, NumberOfBuffers = 3 };
        output.Init(switcher);
        output.Play();

        capture = new WaveInEvent { DeviceNumber = inputDevice, WaveFormat = format, BufferMilliseconds = 20 };
        capture.DataAvailable += Capture_DataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) { log("CAPTURE ERROR: " + e.Exception); status("Capture error: " + e.Exception.Message); }
        };
        capture.StartRecording();
        log($"MIC STARTED — device={inputDevice} format={SampleRate}Hz/{Bits}bit/{Channels}ch");
        status("READY — PTT-gated delayed output is active.");
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (stateLock)
        {
            if (stopped || delayed == null) return;
            double packetStartSeconds = capturePcmSeconds;
            capturePackets++; captureBytes += e.BytesRecorded;
            capturePcmSeconds += e.BytesRecorded / (double)BytesPerSecond;
            if (capturePackets == 1 || capturePackets % 50 == 0)
                log($"MIC CAPTURE — packets={capturePackets} bytes={captureBytes} latest={e.BytesRecorded} ptt={ptt} delayedMode={delayedMode}");

            if (ptt)
            {
                delayed.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                analysisAudio(copy, copy.Length, packetStartSeconds);
            }
            else
            {
                // Preserve the global timeline while hard-gating all non-PTT audio.
                delayed.AddSamples(new byte[e.BytesRecorded], 0, e.BytesRecorded);
            }
        }
    }

    public void AddCensorRegion(double startSeconds, double endSeconds, string word, string? replacementSoundPath = null)
    {
        if (endSeconds <= startSeconds) return;
        lock (censorLock)
        {
            // Whisper's word boundaries can end a few milliseconds before the
            // actual spoken consonant/fricative finishes.  Apply a small safety
            // envelope so the tail of the profanity cannot leak through.
            const double censorPreRollSeconds = 0.060;
            const double censorPostRollSeconds = 0.300;

            double coreStart = Math.Max(0, startSeconds);
            startSeconds = Math.Max(0, startSeconds - censorPreRollSeconds);
            endSeconds += censorPostRollSeconds;

            double outputCursor = switcher?.CurrentSourceSeconds ?? delayedReadSeconds;
            if (endSeconds <= outputCursor)
            {
                log($"CENSOR MISSED — {word} {startSeconds:0.000}s→{endSeconds:0.000}s already passed (outputCursor={outputCursor:0.000}s).");
                return;
            }
            double start = startSeconds, end = endSeconds;

            // Replacement events stay independent so consecutive detections can
            // each restart their assigned sound effect. Merging the expanded
            // safety envelopes would turn several events into one long effect.
            if (!string.IsNullOrWhiteSpace(replacementSoundPath))
            {
                censorRegions.Add(new CensorRegion(start, end, coreStart, word, replacementSoundPath));
            }
            else
            {
                // Mute-only regions can safely be merged.
                var overlaps = censorRegions.Where(r =>
                    string.IsNullOrWhiteSpace(r.ReplacementSoundPath) &&
                    r.EndSeconds >= start - .02 && r.StartSeconds <= end + .02).ToList();
                foreach (var r in overlaps)
                {
                    start = Math.Min(start, r.StartSeconds);
                    end = Math.Max(end, r.EndSeconds);
                    censorRegions.Remove(r);
                }
                censorRegions.Add(new CensorRegion(start, end, coreStart, word, null));
            }
            censorRegions.Sort((a,b) => a.StartSeconds.CompareTo(b.StartSeconds));
            log($"CENSOR SCHEDULED — {word} PCM={start:0.000}s→{end:0.000}s | outputCursor={outputCursor:0.000}s" +
                (string.IsNullOrWhiteSpace(replacementSoundPath) ? "" : $" | sound={Path.GetFileName(replacementSoundPath)}"));
            status($"CENSOR SCHEDULED — {word} {start:0.000}s→{end:0.000}s");
        }
    }

    private CensorRegion? GetCensorRegion(double sourceSeconds)
    {
        lock (censorLock) return censorRegions.FirstOrDefault(r => sourceSeconds >= r.StartSeconds && sourceSeconds < r.EndSeconds);
    }
    private List<CensorRegion> GetCensorRegions(double startSeconds, double endSeconds)
    {
        lock (censorLock) return censorRegions.Where(r => r.EndSeconds > startSeconds && r.StartSeconds < endSeconds).OrderBy(r=>r.StartSeconds).ToList();
    }
    private bool IsCensored(double sourceSeconds)
    {
        lock (censorLock) return censorRegions.Any(r => sourceSeconds >= r.StartSeconds && sourceSeconds < r.EndSeconds);
    }

    public void SetPtt(bool down)
    {
        lock (stateLock)
        {
            if (stopped) return;
            if (down && !ptt)
            {
                ptt = true;
                analysisSegmentStart?.Invoke(capturePcmSeconds);
                log($"PTT DOWN — key=Z | source={capturePcmSeconds:0.000}s");
                status($"PTT HELD — {delaySeconds:0.0}s filtered delay active...");
            }
            else if (!down && ptt)
            {
                ptt = false;
                analysisSegmentEnd?.Invoke(capturePcmSeconds);
                log($"PTT UP — key=Z | source={capturePcmSeconds:0.000}s");
                status("PTT RELEASED — filtered output remains delayed.");
            }
        }
    }

    private (bool delayedMode, bool ptt, bool draining) GetState()
    {
        lock (stateLock) return (delayedMode, ptt, false);
    }

    public void Stop()
    {
        lock (stateLock) { stopped=true; ptt=false; delayedMode=false; }
        try { capture?.StopRecording(); } catch { }
        try { output?.Stop(); } catch { }
        delayed?.ClearBuffer();
        lock (censorLock) censorRegions.Clear();
    }

    public void Dispose()
    {
        Stop();
        if (capture != null) { capture.DataAvailable -= Capture_DataAvailable; capture.Dispose(); capture=null; }
        output?.Dispose(); output=null;
    }

    private sealed class CensorRegion
    {
        public double StartSeconds {get;}
        public double EndSeconds {get;}
        public double EffectStartSeconds {get;}
        public string Word {get;}
        public string? ReplacementSoundPath {get;}
        public CensorRegion(double start, double end, double effectStart, string word, string? sound)
        {
            StartSeconds=start; EndSeconds=end; EffectStartSeconds=effectStart; Word=word; ReplacementSoundPath=sound;
        }
    }

    private sealed class SwitchProvider : IWaveProvider
    {
        private readonly WaveFormat format;
        private readonly BufferedWaveProvider delayed;
        private readonly Func<(bool delayedMode,bool ptt,bool draining)> state;
        private readonly Func<double> delay;
        private readonly Func<double> captureSeconds;
        private readonly Func<double,bool> isCensored;
        private readonly Func<double,double,List<CensorRegion>> getCensorRegions;
        private readonly Func<double,CensorRegion?> getCensorRegion;
        private readonly Action<string> censorStatus;
        private readonly Action<string> log;
        private double sourceReadSeconds;
        private double lastOutputLogSecond=-1;
        private readonly Dictionary<string,byte[]> replacementCache=new(StringComparer.OrdinalIgnoreCase);

        public SwitchProvider(WaveFormat format, BufferedWaveProvider delayed,
            Func<(bool delayedMode,bool ptt,bool draining)> state, Func<double> delay, Func<double> captureSeconds,
            Func<double,bool> isCensored, Func<double,double,List<CensorRegion>> getCensorRegions,
            Func<double,CensorRegion?> getCensorRegion, Action<string> censorStatus, Action<string> log)
        {
            this.format=format; this.delayed=delayed; this.state=state; this.delay=delay; this.captureSeconds=captureSeconds;
            this.isCensored=isCensored; this.getCensorRegions=getCensorRegions; this.getCensorRegion=getCensorRegion;
            this.censorStatus=censorStatus; this.log=log;
        }
        public WaveFormat WaveFormat=>format;
        public double CurrentSourceSeconds=>sourceReadSeconds;
        public int Read(byte[] buffer,int offset,int count)
        {
            var s=state();
            if(!s.delayedMode) { Array.Clear(buffer,offset,count); return count; }

            double safeSource=Math.Max(0,captureSeconds()-delay());
            if(sourceReadSeconds>=safeSource) { Array.Clear(buffer,offset,count); return count; }

            int read=delayed.Read(buffer,offset,count);
            if(read<=0) { Array.Clear(buffer,offset,count); return count; }
            if(read<count) Array.Clear(buffer,offset+read,count-read);

            double start=sourceReadSeconds;
            double end=start+read/(double)format.AverageBytesPerSecond;
            if(Math.Floor(start)>lastOutputLogSecond)
            {
                lastOutputLogSecond=Math.Floor(start);
                log($"DELAYED OUTPUT CURSOR — source={start:0.000}s | bytes={read} | censorRegions={(isCensored(start) ? "ACTIVE":"none")}");
            }

            var activeRegions = getCensorRegions(start, end);

            // Mute all safety envelopes first. This guarantees no microphone audio
            // can leak through when multiple censor envelopes overlap.
            foreach(var region in activeRegions)
            {
                double a=Math.Max(start,region.StartSeconds), b=Math.Min(end,region.EndSeconds);
                if(b<=a) continue;
                int relStart=(int)Math.Floor((a-start)*format.AverageBytesPerSecond);
                int relEnd=(int)Math.Ceiling((b-start)*format.AverageBytesPerSecond);
                relStart=Math.Clamp(relStart,0,read); relEnd=Math.Clamp(relEnd,relStart,read);
                relStart-=relStart%2; relEnd-=relEnd%2;
                int n=relEnd-relStart; if(n<=0) continue;
                Array.Clear(buffer,offset+relStart,n);
            }

            // Overlay replacement effects independently. Each censor event gets
            // its own effect start, even when the safety envelopes overlap.
            foreach(var region in activeRegions)
            {
                if(string.IsNullOrWhiteSpace(region.ReplacementSoundPath)) continue;
                if(!replacementCache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm=LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    replacementCache[region.ReplacementSoundPath]=pcm;
                }
                if(pcm.Length==0) continue;

                double effectStart=Math.Max(start,region.EffectStartSeconds);
                double effectEnd=Math.Min(end,region.EffectStartSeconds + pcm.Length/(double)format.AverageBytesPerSecond);
                if(effectEnd<=effectStart) continue;

                int relStart=(int)Math.Floor((effectStart-start)*format.AverageBytesPerSecond);
                int relEnd=(int)Math.Ceiling((effectEnd-start)*format.AverageBytesPerSecond);
                relStart=Math.Clamp(relStart,0,read); relEnd=Math.Clamp(relEnd,relStart,read);
                relStart-=relStart%2; relEnd-=relEnd%2;
                int n=relEnd-relStart; if(n<=0) continue;

                TryWriteReplacementRange(buffer,offset+relStart,n,region,effectStart-region.EffectStartSeconds);
            }

            sourceReadSeconds += read/(double)format.AverageBytesPerSecond;
            return count;
        }

        
        private bool TryWriteReplacementSample(byte[] buffer, int destinationOffset, int bytes, CensorRegion region, double sourceTime)
        {
            if (string.IsNullOrWhiteSpace(region.ReplacementSoundPath) || bytes < 2)
                return false;

            try
            {
                if (!replacementCache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm = LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    replacementCache[region.ReplacementSoundPath] = pcm;
                }

                if (pcm.Length == 0)
                    return false;

                int sourceByte = (int)Math.Round((sourceTime - region.StartSeconds) * format.AverageBytesPerSecond);
                sourceByte -= sourceByte % (format.BitsPerSample / 8);
                if (sourceByte < 0 || sourceByte >= pcm.Length)
                    return false;

                int copyBytes = Math.Min(bytes, pcm.Length - sourceByte);
                copyBytes -= copyBytes % (format.BitsPerSample / 8);
                if (copyBytes <= 0)
                    return false;

                Buffer.BlockCopy(pcm, sourceByte, buffer, destinationOffset, copyBytes);
                if (copyBytes < bytes)
                    Array.Clear(buffer, destinationOffset + copyBytes, bytes - copyBytes);
                return true;
            }
            catch (Exception ex)
            {
                log($"REPLACEMENT SOUND ERROR — {Path.GetFileName(region.ReplacementSoundPath)} — {ex.Message}");
                replacementCache[region.ReplacementSoundPath] = Array.Empty<byte>();
                return false;
            }
        }

        private bool TryWriteReplacementRange(byte[] buffer, int destinationOffset, int bytes, CensorRegion region, double replacementOffsetSeconds)
        {
            if (string.IsNullOrWhiteSpace(region.ReplacementSoundPath) || bytes <= 0)
                return false;

            try
            {
                if (!replacementCache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm = LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    replacementCache[region.ReplacementSoundPath] = pcm;
                }

                if (pcm.Length == 0)
                    return false;

                int sourceByte = (int)Math.Floor(Math.Max(0, replacementOffsetSeconds) * format.AverageBytesPerSecond);
                sourceByte -= sourceByte % 2;
                if (sourceByte >= pcm.Length)
                    return false;

                int copyBytes = Math.Min(bytes, pcm.Length - sourceByte);
                copyBytes -= copyBytes % 2;
                if (copyBytes <= 0)
                    return false;

                Buffer.BlockCopy(pcm, sourceByte, buffer, destinationOffset, copyBytes);

                // Never let uncopied bytes expose the original microphone audio.
                if (copyBytes < bytes)
                    Array.Clear(buffer, destinationOffset + copyBytes, bytes - copyBytes);

                return true;
            }
            catch (Exception ex)
            {
                log($"REPLACEMENT SOUND ERROR — {Path.GetFileName(region.ReplacementSoundPath)} — {ex.Message}");
                replacementCache[region.ReplacementSoundPath] = Array.Empty<byte>();
                return false;
            }
        }

        // Converts common WAV formats to the exact VoiceGuard output format:
        // 48kHz / mono / 16-bit PCM.  This means users do NOT have to pre-convert
        // their replacement sounds before assigning them to a blocked word.
        private byte[] LoadReplacementAsOutputPcm(string path)
        {
            using var reader = new WaveFileReader(path);
            var wf = reader.WaveFormat;
            if (wf.Channels < 1 || wf.SampleRate < 1 || wf.BitsPerSample < 1)
            {
                log($"REPLACEMENT SOUND REJECTED — {Path.GetFileName(path)} has an unsupported WAV format");
                return Array.Empty<byte>();
            }

            if (wf.Encoding != WaveFormatEncoding.Pcm && wf.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                log($"REPLACEMENT SOUND REJECTED — {Path.GetFileName(path)} encoding={wf.Encoding}; supported: PCM or IEEE float WAV");
                return Array.Empty<byte>();
            }

            if (wf.BitsPerSample != 8 && wf.BitsPerSample != 16 && wf.BitsPerSample != 24 &&
                wf.BitsPerSample != 32 && wf.BitsPerSample != 64)
            {
                log($"REPLACEMENT SOUND REJECTED — {Path.GetFileName(path)} bit depth={wf.BitsPerSample}; supported: 8/16/24/32/64-bit WAV");
                return Array.Empty<byte>();
            }

            int bytesPerInputSample = wf.BitsPerSample / 8;
            int frameBytes = bytesPerInputSample * wf.Channels;
            if (frameBytes <= 0)
                return Array.Empty<byte>();

            long maxFramesLong = reader.Length / frameBytes;
            if (maxFramesLong <= 0 || maxFramesLong > int.MaxValue)
                return Array.Empty<byte>();
            int inputFrames = (int)maxFramesLong;

            var mono = new float[inputFrames];
            byte[] raw = new byte[(int)reader.Length];
            int rawRead = 0;
            while (rawRead < raw.Length)
            {
                int n = reader.Read(raw, rawRead, raw.Length - rawRead);
                if (n <= 0) break;
                rawRead += n;
            }

            for (int frame = 0; frame < inputFrames; frame++)
            {
                int frameOffset = frame * frameBytes;
                double sum = 0;
                for (int ch = 0; ch < wf.Channels; ch++)
                    sum += DecodeReplacementSample(raw, frameOffset + ch * bytesPerInputSample, wf.BitsPerSample, wf.Encoding);
                mono[frame] = Math.Clamp((float)(sum / wf.Channels), -1f, 1f);
            }

            int outputFrames = Math.Max(1, (int)Math.Round(inputFrames * (format.SampleRate / (double)wf.SampleRate)));
            byte[] output = new byte[outputFrames * 2];
            double ratio = wf.SampleRate / (double)format.SampleRate;

            for (int i = 0; i < outputFrames; i++)
            {
                double src = i * ratio;
                int i0 = (int)Math.Floor(src);
                int i1 = Math.Min(i0 + 1, mono.Length - 1);
                i0 = Math.Clamp(i0, 0, mono.Length - 1);
                float frac = (float)(src - Math.Floor(src));
                float sample = mono[i0] + (mono[i1] - mono[i0]) * frac;
                short pcm16 = (short)Math.Clamp((int)Math.Round(sample * 32767.0), short.MinValue, short.MaxValue);
                output[i * 2] = (byte)(pcm16 & 0xFF);
                output[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }

            log($"REPLACEMENT SOUND LOADED — {Path.GetFileName(path)} | {wf.SampleRate}Hz/{wf.BitsPerSample}bit/{wf.Channels}ch/{wf.Encoding} → 48000Hz/16bit/mono | {outputFrames / 48000.0:0.000}s");
            return output;
        }

        private static float DecodeReplacementSample(byte[] raw, int offset, int bits, WaveFormatEncoding encoding)
        {
            if (encoding == WaveFormatEncoding.IeeeFloat)
            {
                if (bits == 32)
                    return BitConverter.ToSingle(raw, offset);
                if (bits == 64)
                    return (float)BitConverter.ToDouble(raw, offset);
            }

            if (bits == 8)
                return (raw[offset] - 128) / 128f;
            if (bits == 16)
                return BitConverter.ToInt16(raw, offset) / 32768f;
            if (bits == 24)
            {
                int value = raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            if (bits == 32)
                return BitConverter.ToInt32(raw, offset) / 2147483648f;

            throw new InvalidDataException($"Unsupported PCM bit depth: {bits}");
        }


    }
}
