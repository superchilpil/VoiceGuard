using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace VoiceGuard;

public readonly record struct ReplacementPlaybackSettings(bool MatchWordLength, double DurationSeconds);

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
    private readonly Func<string, ReplacementPlaybackSettings>? replacementPlaybackResolver;
    private readonly Func<string, double>? replacementVolumeResolver;
    private readonly Func<double>? outputVolumeResolver;
    private readonly Func<string, double>? soundboardVolumeResolver;
    private readonly Func<string, string?>? soundboardSoundResolver;

    private WaveInEvent? capture;
    private WaveOutEvent? output;
    private BufferedWaveProvider? delayed;
    private BufferedWaveProvider? live;
    private SwitchProvider? switcher;

    private readonly object stateLock = new();
    private readonly object censorLock = new();
    private readonly List<CensorRegion> censorRegions = new();
    private bool ptt;
    private bool delayedMode;
    private bool draining;
    private double drainTargetSeconds;
    private bool stopped;
    private bool soundboardListening;
    private long capturePackets;
    private long captureBytes;
    private double capturePcmSeconds;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int Bits = 16;
    private const int BytesPerSecond = SampleRate * Channels * Bits / 8;
    private const double MaxReplacementDurationSeconds = 5.0; // profanity replacements only

    public AudioEngine(
        int inputDevice, int outputDevice, double delaySeconds, Action<string> status,
        Action<byte[], int, double> analysisAudio, Action<string>? logCallback = null,
        Action<double>? analysisSegmentStart = null, Action<double>? analysisSegmentEnd = null,
        Func<double>? analysisCompletedSeconds = null, Func<bool>? analysisHasPending = null,
        Func<double>? analysisSafeThroughSeconds = null, Func<string, string?>? replacementSoundResolver = null,
        Func<string, ReplacementPlaybackSettings>? replacementPlaybackResolver = null,
        Func<string, double>? replacementVolumeResolver = null,
        Func<double>? outputVolumeResolver = null, Func<string, string?>? soundboardSoundResolver = null,
        Func<string, double>? soundboardVolumeResolver = null)
    {
        this.inputDevice = inputDevice; this.outputDevice = outputDevice; this.delaySeconds = delaySeconds;
        this.status = status; this.analysisAudio = analysisAudio;
        this.analysisSegmentStart = analysisSegmentStart; this.analysisSegmentEnd = analysisSegmentEnd;
        this.analysisCompletedSeconds = analysisCompletedSeconds; this.analysisHasPending = analysisHasPending;
        this.analysisSafeThroughSeconds = analysisSafeThroughSeconds;
        this.replacementSoundResolver = replacementSoundResolver;
        this.replacementPlaybackResolver = replacementPlaybackResolver;
        this.replacementVolumeResolver = replacementVolumeResolver;
        this.outputVolumeResolver = outputVolumeResolver;
        this.soundboardSoundResolver = soundboardSoundResolver;
        this.soundboardVolumeResolver = soundboardVolumeResolver;
        this.log = logCallback ?? (_ => { });
    }

    public double CurrentSourceSeconds => switcher?.CurrentSourceSeconds ?? 0.0;
    public double CapturePcmSeconds => capturePcmSeconds;
    public double DelaySeconds => delaySeconds;


    public bool SetSoundboardListening(bool enabled)
    {
        lock (stateLock)
        {
            soundboardListening = enabled && !stopped && !ptt && !delayedMode;
            return soundboardListening;
        }
    }

    public double TriggerSoundboardNow(string phrase)
    {
        var soundPath = soundboardSoundResolver?.Invoke(phrase);
        if (string.IsNullOrWhiteSpace(soundPath) || !File.Exists(soundPath))
            return 0.0;

        double start = CapturePcmSeconds + 0.020;
        double duration = GetAudioDurationSeconds(soundPath);
        if (duration <= 0.0)
            return 0.0;

        AddSoundboardRegion(start, start + 0.050, phrase, soundPath);
        return duration;
    }

    public void SetOutputVolume(double volume)
    {
        switcher?.SetOutputVolume(volume);
    }

    public void Start()
    {
        var format = new WaveFormat(SampleRate, Bits, Channels);
        delayed = new BufferedWaveProvider(format)
        {
            BufferLength = BytesPerSecond * 30,
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };

        live = new BufferedWaveProvider(format)
        {
            BufferLength = BytesPerSecond * 2,
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };

        // PTT is idle at startup: microphone audio passes through live.
        // The delayed/censored path is activated only while PTT is held and
        // remains active briefly while the final delayed PTT audio drains.
        delayedMode = false;
        draining = false;
        drainTargetSeconds = 0;

        switcher = new SwitchProvider(
            format, delayed, live, FinishDrain, GetState, () => delaySeconds, () => capturePcmSeconds, () => drainTargetSeconds, GetReplacementDrainTargetSeconds,
            IsCensored, GetCensorRegions, GetCensorRegion, status, log, replacementVolumeResolver, outputVolumeResolver, soundboardVolumeResolver);

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
            else if (soundboardListening && !delayedMode)
            {
                // Soundboard phrase listening is private: keep normal live
                // passthrough active, but feed the microphone to the dedicated
                // phrase recognizer without entering the delayed/PTT path.
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                analysisAudio(copy, copy.Length, packetStartSeconds);
            }
            else if (delayedMode)
            {
                // During the drain after PTT release, keep the delayed timeline
                // moving with silence. Do not queue live audio yet, otherwise it
                // would be replayed after the delayed PTT audio finishes.
                delayed.AddSamples(new byte[e.BytesRecorded], 0, e.BytesRecorded);
            }
            else
            {
                // Normal PTT-off operation: pass the microphone through live.
                switcher?.AddLiveSamples(e.Buffer, 0, e.BytesRecorded);
            }
        }
    }

    public void AddCensorRegion(double startSeconds, double endSeconds, string word, string? replacementSoundPath = null, ReplacementPlaybackSettings? playbackSettings = null)
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
            double coreEnd = Math.Max(coreStart, endSeconds);
            startSeconds = Math.Max(0, startSeconds - censorPreRollSeconds);
            endSeconds += censorPostRollSeconds;

            double outputCursor = switcher?.CurrentSourceSeconds ?? 0.0;
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
                var settings = playbackSettings ?? new ReplacementPlaybackSettings(true, 1.0);
                double duration = settings.MatchWordLength
                    ? Math.Max(0.0, coreEnd - coreStart)
                    : Math.Clamp(settings.DurationSeconds, 0.1, MaxReplacementDurationSeconds);
                double replacementEnd = coreStart + duration;
                censorRegions.Add(new CensorRegion(start, end, coreStart, replacementEnd, word, replacementSoundPath, false));
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
                censorRegions.Add(new CensorRegion(start, end, coreStart, coreStart, word, null, false));
            }
            censorRegions.Sort((a,b) => a.StartSeconds.CompareTo(b.StartSeconds));
            log($"CENSOR SCHEDULED — {word} PCM={start:0.000}s→{end:0.000}s | outputCursor={outputCursor:0.000}s" +
                (string.IsNullOrWhiteSpace(replacementSoundPath) ? "" : $" | sound={Path.GetFileName(replacementSoundPath)}"));
            status($"CENSOR SCHEDULED — {word} {start:0.000}s→{end:0.000}s");
        }
    }

    private CensorRegion? GetCensorRegion(double sourceSeconds)
    {
        lock (censorLock) return censorRegions.FirstOrDefault(r => sourceSeconds >= r.StartSeconds && sourceSeconds < Math.Max(r.EndSeconds, r.ReplacementEndSeconds));
    }
    private List<CensorRegion> GetCensorRegions(double startSeconds, double endSeconds)
    {
        lock (censorLock) return censorRegions.Where(r => Math.Max(r.EndSeconds, r.ReplacementEndSeconds) > startSeconds && r.StartSeconds < endSeconds).OrderBy(r=>r.StartSeconds).ToList();
    }
    private bool IsCensored(double sourceSeconds)
    {
        lock (censorLock) return censorRegions.Any(r => sourceSeconds >= r.StartSeconds && sourceSeconds < Math.Max(r.EndSeconds, r.ReplacementEndSeconds));
    }

    public void AddSoundboardRegion(double startSeconds, double endSeconds, string phrase, string soundPath)
    {
        if (endSeconds <= startSeconds || string.IsNullOrWhiteSpace(soundPath)) return;
        lock (censorLock)
        {
            const double censorPreRollSeconds = 0.060;
            const double censorPostRollSeconds = 0.300;
            double coreStart = Math.Max(0, startSeconds);
            double coreEnd = Math.Max(coreStart, endSeconds);
            startSeconds = Math.Max(0, coreStart - censorPreRollSeconds);
            endSeconds = coreEnd + censorPostRollSeconds;
            double outputCursor = switcher?.CurrentSourceSeconds ?? 0.0;
            if (endSeconds <= outputCursor) { log($"SOUNDBOARD MISSED — {phrase} already passed."); return; }
            double clipDuration = GetAudioDurationSeconds(soundPath);
            if (clipDuration <= 0) return;
            censorRegions.Add(new CensorRegion(startSeconds, endSeconds, coreStart, coreStart + clipDuration, phrase, soundPath, true));
            if (delayed != null)
            {
                long required = (long)Math.Ceiling((clipDuration + delaySeconds + 5.0) * BytesPerSecond);
                required = Math.Clamp(required, BytesPerSecond * 30L, BytesPerSecond * 300L);
                delayed.BufferLength = (int)Math.Min(int.MaxValue, Math.Max(delayed.BufferLength, required));
                var currentState = GetState();
                if (currentState.draining)
                {
                    long silenceBytes = Math.Min(BytesPerSecond * 300L, (long)Math.Ceiling((clipDuration + 0.5) * BytesPerSecond));
                    silenceBytes -= silenceBytes % 2;
                    if (silenceBytes > 0) delayed.AddSamples(new byte[(int)Math.Min(int.MaxValue - 1L, silenceBytes)], 0, (int)Math.Min(int.MaxValue - 1L, silenceBytes));
                }
            }
            censorRegions.Sort((a,b) => a.StartSeconds.CompareTo(b.StartSeconds));
            log($"SOUNDBOARD SCHEDULED — {phrase} | sound={Path.GetFileName(soundPath)} | duration={clipDuration:0.000}s");
            status($"SOUNDBOARD — {phrase}");
        }
    }

    private static double GetAudioDurationSeconds(string path)
    {
        try { using var reader = new WaveFileReader(path); return reader.TotalTime.TotalSeconds; }
        catch { return 0; }
    }

    public void SetPtt(bool down)
    {
        lock (stateLock)
        {
            if (stopped) return;

            if (down && !ptt)
            {
                // A real PTT transmission always owns the analysis path.
                // Soundboard phrase listening must be released before PTT starts.
                soundboardListening = false;

                // Start a fresh delayed PTT segment. The output cursor begins
                // delaySeconds behind the current capture clock, so the first
                // PTT audio reaches the output only after the configured delay.
                ptt = true;
                draining = false;
                delayedMode = true;
                drainTargetSeconds = 0;

                delayed?.ClearBuffer();
                switcher?.ClearLiveBuffer();

                double baseSeconds = Math.Max(0, capturePcmSeconds - delaySeconds);
                switcher?.BeginDelayedTimeline(baseSeconds);

                // Fill the delay window with silence. Newly captured PTT audio
                // is appended after this silence and therefore stays delayed.
                int silenceBytes = (int)Math.Round(delaySeconds * BytesPerSecond);
                silenceBytes -= silenceBytes % 2;
                if (silenceBytes > 0)
                    delayed?.AddSamples(new byte[silenceBytes], 0, silenceBytes);

                analysisSegmentStart?.Invoke(capturePcmSeconds);
                log($"PTT DOWN — key=Z | source={capturePcmSeconds:0.000}s");
                log($"DELAYED OUTPUT TIMELINE — base={baseSeconds:0.000}s | safetyDelay={delaySeconds:0.0}s");
                status($"PTT HELD — {delaySeconds:0.0}s filtered delay active...");
            }
            else if (!down && ptt)
            {
                ptt = false;
                draining = true;
                delayedMode = true;
                drainTargetSeconds = capturePcmSeconds;

                // Queue enough silence for replacement/soundboard effects that
                // extend beyond the captured PTT timeline. Soundboard clips are
                // intentionally not limited to the legacy 5-second replacement cap.
                double tailSeconds = GetReplacementDrainTargetSeconds() - capturePcmSeconds;
                // Do not hold the PTT/delayed timeline for the legacy 5-second
                // replacement limit when there is no effect that actually needs it.
                // The drain target itself already accounts for long soundboard clips.
                tailSeconds = Math.Max(0.25, tailSeconds + 0.25);
                long tailBytesLong = (long)Math.Ceiling(tailSeconds * BytesPerSecond);
                tailBytesLong = Math.Min(tailBytesLong, BytesPerSecond * 300L);
                int replacementTailBytes = (int)Math.Min(int.MaxValue - 1L, tailBytesLong);
                replacementTailBytes -= replacementTailBytes % 2;
                if (replacementTailBytes > 0)
                    delayed?.AddSamples(new byte[replacementTailBytes], 0, replacementTailBytes);

                analysisSegmentEnd?.Invoke(capturePcmSeconds);
                log($"PTT UP — key=Z | source={capturePcmSeconds:0.000}s");
                status("PTT RELEASED — finishing delayed filtered audio...");
            }
        }
    }

    private (bool delayedMode, bool ptt, bool draining) GetState()
    {
        lock (stateLock) return (delayedMode, ptt, draining);
    }

    private double GetReplacementDrainTargetSeconds()
    {
        lock (censorLock)
        {
            return censorRegions.Count == 0
                ? 0.0
                : censorRegions.Max(r => r.ReplacementEndSeconds);
        }
    }

    private void FinishDrain()
    {
        lock (stateLock)
        {
            if (!draining) return;
            draining = false;
            delayedMode = false;
            drainTargetSeconds = 0;
            delayed?.ClearBuffer();
            switcher?.ClearLiveBuffer();
            status("READY — PTT-gated delayed output is active.");
            log("OUTPUT TIMELINE RESUME — live pass-through");
        }
    }

    public void Stop()
    {
        lock (stateLock) { stopped=true; ptt=false; delayedMode=false; draining=false; soundboardListening=false; }
        try { capture?.StopRecording(); } catch { }
        try { output?.Stop(); } catch { }
        delayed?.ClearBuffer();
        live?.ClearBuffer();
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
        public double ReplacementEndSeconds {get;}
        public string Word {get;}
        public string? ReplacementSoundPath {get;}
        public bool IsSoundboard { get; }
        public CensorRegion(double start, double end, double effectStart, double replacementEnd, string word, string? sound, bool isSoundboard = false)
        {
            StartSeconds=start; EndSeconds=end; EffectStartSeconds=effectStart; ReplacementEndSeconds=replacementEnd; Word=word; ReplacementSoundPath=sound; IsSoundboard=isSoundboard;
        }
    }

    private sealed class SwitchProvider : IWaveProvider
    {
        private readonly WaveFormat format;
        private readonly BufferedWaveProvider delayed;
        private readonly BufferedWaveProvider live;
        private readonly Action finishDrain;
        private readonly Func<(bool delayedMode,bool ptt,bool draining)> state;
        private readonly Func<double> delay;
        private readonly Func<double> captureSeconds;
        private readonly Func<double> getDrainTargetSeconds;
        private readonly Func<double> getReplacementDrainTargetSeconds;
        private readonly Func<double,bool> isCensored;
        private readonly Func<double,double,List<CensorRegion>> getCensorRegions;
        private readonly Func<double,CensorRegion?> getCensorRegion;
        private readonly Action<string> censorStatus;
        private readonly Action<string> log;
        private readonly Func<string, double>? replacementVolumeResolver;
        private readonly Func<double>? outputVolumeResolver;
        private readonly Func<string, double>? soundboardVolumeResolver;
    private readonly Func<string, string?>? soundboardSoundResolver;
        private double outputVolume = 1.0;
        private double sourceReadSeconds;
        private double lastOutputLogSecond=-1;
        private readonly Dictionary<string,byte[]> replacementCache=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,byte[]> soundboardCache=new(StringComparer.OrdinalIgnoreCase);

        public SwitchProvider(WaveFormat format, BufferedWaveProvider delayed, BufferedWaveProvider live,
            Action finishDrain, Func<(bool delayedMode,bool ptt,bool draining)> state, Func<double> delay, Func<double> captureSeconds, Func<double> getDrainTargetSeconds, Func<double> getReplacementDrainTargetSeconds,
            Func<double,bool> isCensored, Func<double,double,List<CensorRegion>> getCensorRegions,
            Func<double,CensorRegion?> getCensorRegion, Action<string> censorStatus, Action<string> log, Func<string, double>? replacementVolumeResolver, Func<double>? outputVolumeResolver, Func<string, double>? soundboardVolumeResolver)
        {
            this.format=format; this.delayed=delayed; this.live=live; this.finishDrain=finishDrain; this.state=state; this.delay=delay; this.captureSeconds=captureSeconds; this.getDrainTargetSeconds=getDrainTargetSeconds; this.getReplacementDrainTargetSeconds=getReplacementDrainTargetSeconds;
            this.isCensored=isCensored; this.getCensorRegions=getCensorRegions; this.getCensorRegion=getCensorRegion;
            this.censorStatus=censorStatus; this.log=log; this.replacementVolumeResolver=replacementVolumeResolver; this.outputVolumeResolver=outputVolumeResolver; this.soundboardVolumeResolver=soundboardVolumeResolver;
            outputVolume = Math.Clamp(outputVolumeResolver?.Invoke() ?? 1.0, 0.0, 1.5);
        }
        public WaveFormat WaveFormat=>format;
        public double CurrentSourceSeconds=>sourceReadSeconds;
        public void AddLiveSamples(byte[] data, int offset, int count) => live.AddSamples(data, offset, count);
        public void ClearLiveBuffer() => live.ClearBuffer();
        public void BeginDelayedTimeline(double baseSeconds) => sourceReadSeconds = Math.Max(0, baseSeconds);
        public void SetOutputVolume(double volume) => outputVolume = Math.Clamp(volume, 0.0, 1.5);
        private void ApplyOutputVolume(byte[] buffer, int offset, int bytes)
        {
            double volume = Math.Clamp(outputVolumeResolver?.Invoke() ?? outputVolume, 0.0, 1.5);
            outputVolume = volume;
            if (Math.Abs(volume - 1.0) < 0.0001) return;
            int end = offset + (bytes - (bytes % 2));
            for (int i = offset; i < end; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                double normalized = sample / 32768.0;
                double processed = volume <= 1.0 ? normalized * volume : Math.Tanh(normalized * volume) / Math.Tanh(volume);
                int scaled = (int)Math.Round(Math.Clamp(processed, -1.0, 1.0) * 32767.0);
                buffer[i] = (byte)(scaled & 0xFF);
                buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        public int Read(byte[] buffer,int offset,int count)
        {
            var s=state();

            if(!s.delayedMode)
            {
                int liveRead = live.Read(buffer, offset, count);
                if(liveRead < count)
                    Array.Clear(buffer, offset + liveRead, count - liveRead);
                ApplyOutputVolume(buffer, offset, count);
                return count;
            }

            double safeSource = s.draining
                ? Math.Max(getDrainTargetSeconds(), getReplacementDrainTargetSeconds())
                : Math.Max(0, captureSeconds() - delay());

            if(sourceReadSeconds>=safeSource)
            {
                if(s.draining)
                    finishDrain();
                Array.Clear(buffer,offset,count);
                return count;
            }

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
                var cache = region.IsSoundboard ? soundboardCache : replacementCache;
                if(!cache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm = region.IsSoundboard ? LoadSoundboardAsOutputPcm(region.ReplacementSoundPath) : LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    cache[region.ReplacementSoundPath]=pcm;
                }
                if(pcm.Length==0) continue;

                double effectStart=Math.Max(start,region.EffectStartSeconds);
                double effectEnd=Math.Min(end, Math.Min(
                    region.ReplacementEndSeconds,
                    region.EffectStartSeconds + pcm.Length/(double)format.AverageBytesPerSecond));
                if(effectEnd<=effectStart) continue;

                int relStart=(int)Math.Floor((effectStart-start)*format.AverageBytesPerSecond);
                int relEnd=(int)Math.Ceiling((effectEnd-start)*format.AverageBytesPerSecond);
                relStart=Math.Clamp(relStart,0,read); relEnd=Math.Clamp(relEnd,relStart,read);
                relStart-=relStart%2; relEnd-=relEnd%2;
                int n=relEnd-relStart; if(n<=0) continue;

                TryWriteReplacementRange(buffer,offset+relStart,n,region,effectStart-region.EffectStartSeconds);
            }

            sourceReadSeconds += read/(double)format.AverageBytesPerSecond;
            ApplyOutputVolume(buffer, offset, count);
            return count;
        }

        
        private bool TryWriteReplacementSample(byte[] buffer, int destinationOffset, int bytes, CensorRegion region, double sourceTime)
        {
            if (string.IsNullOrWhiteSpace(region.ReplacementSoundPath) || bytes < 2)
                return false;

            try
            {
                var cache = region.IsSoundboard ? soundboardCache : replacementCache;
                if (!cache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm = region.IsSoundboard ? LoadSoundboardAsOutputPcm(region.ReplacementSoundPath) : LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    cache[region.ReplacementSoundPath] = pcm;
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
                var cache = region.IsSoundboard ? soundboardCache : replacementCache;
                if (!cache.TryGetValue(region.ReplacementSoundPath, out var pcm))
                {
                    pcm = region.IsSoundboard
                        ? LoadSoundboardAsOutputPcm(region.ReplacementSoundPath)
                        : LoadReplacementAsOutputPcm(region.ReplacementSoundPath);
                    cache[region.ReplacementSoundPath] = pcm;
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

                double volume = Math.Clamp((region.IsSoundboard ? soundboardVolumeResolver?.Invoke(region.Word) : replacementVolumeResolver?.Invoke(region.Word)) ?? 1.0, 0.0, 1.5);
                if (Math.Abs(volume - 1.0) > 0.0001)
                {
                    int end = destinationOffset + copyBytes;
                    for (int i = destinationOffset; i < end; i += 2)
                    {
                        short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                        double normalized = sample / 32768.0;
                        double processed = volume <= 1.0 ? normalized * volume : Math.Tanh(normalized * volume) / Math.Tanh(volume);
                        int scaled = (int)Math.Round(Math.Clamp(processed, -1.0, 1.0) * 32767.0);
                        buffer[i] = (byte)(scaled & 0xFF);
                        buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
                    }
                }

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
        private byte[] LoadSoundboardAsOutputPcm(string path)
        {
            // Soundboard clips have NO five-second limit. This loader is deliberately
            // separate from the profanity replacement loader, which retains its 5s cap.
            using var reader = new WaveFileReader(path);
            var wf = reader.WaveFormat;
            if (wf.Encoding != WaveFormatEncoding.Pcm && wf.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                log($"SOUNDBOARD SOUND REJECTED — {Path.GetFileName(path)} encoding={wf.Encoding}");
                return Array.Empty<byte>();
            }

            if (wf.SampleRate == 48000 && wf.Channels == 1 && wf.BitsPerSample == 16 && wf.Encoding == WaveFormatEncoding.Pcm)
            {
                using var ms = new MemoryStream();
                reader.CopyTo(ms);
                var pcm = ms.ToArray();
                log($"SOUNDBOARD SOUND LOADED — {Path.GetFileName(path)} | {pcm.Length / (double)BytesPerSecond:0.000}s | full file");
                return pcm;
            }

            int bytesPerInputSample = wf.BitsPerSample / 8;
            int frameBytes = bytesPerInputSample * wf.Channels;
            if (bytesPerInputSample <= 0 || frameBytes <= 0 || wf.SampleRate <= 0)
                return Array.Empty<byte>();

            long totalFramesLong = reader.Length / frameBytes;
            if (totalFramesLong <= 0 || totalFramesLong > int.MaxValue)
                return Array.Empty<byte>();

            int inputFrames = (int)totalFramesLong;
            var mono = new float[inputFrames];
            byte[] raw = new byte[inputFrames * frameBytes];
            int rawRead = 0;
            while (rawRead < raw.Length)
            {
                int n = reader.Read(raw, rawRead, raw.Length - rawRead);
                if (n <= 0) break;
                rawRead += n;
            }
            inputFrames = Math.Min(inputFrames, rawRead / frameBytes);
            if (inputFrames <= 0) return Array.Empty<byte>();

            for (int frame = 0; frame < inputFrames; frame++)
            {
                int frameOffset = frame * frameBytes;
                double sum = 0;
                for (int ch = 0; ch < wf.Channels; ch++)
                    sum += DecodeReplacementSample(raw, frameOffset + ch * bytesPerInputSample, wf.BitsPerSample, wf.Encoding);
                mono[frame] = Math.Clamp((float)(sum / wf.Channels), -1f, 1f);
            }

            int outputFrames = Math.Max(1, (int)Math.Round(inputFrames * (48000.0 / wf.SampleRate)));
            byte[] output = new byte[outputFrames * 2];
            double ratio = wf.SampleRate / 48000.0;
            for (int i = 0; i < outputFrames; i++)
            {
                double src = i * ratio;
                int i0 = Math.Clamp((int)Math.Floor(src), 0, mono.Length - 1);
                int i1 = Math.Min(i0 + 1, mono.Length - 1);
                float frac = (float)(src - Math.Floor(src));
                float sample = mono[i0] + (mono[i1] - mono[i0]) * frac;
                short pcm16 = (short)Math.Clamp((int)Math.Round(sample * 32767.0), short.MinValue, short.MaxValue);
                output[i * 2] = (byte)(pcm16 & 0xFF);
                output[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }
            log($"SOUNDBOARD SOUND LOADED — {Path.GetFileName(path)} | {outputFrames / 48000.0:0.000}s | full file");
            return output;
        }

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
            if (maxFramesLong <= 0)
                return Array.Empty<byte>();

            // Runtime safety: never even load more than the first five seconds
            // of a replacement WAV into memory. This protects against existing
            // or manually assigned WAV files that predate the import limiter.
            long maxInputFrames = (long)Math.Ceiling(wf.SampleRate * MaxReplacementDurationSeconds);
            int inputFrames = (int)Math.Min(maxFramesLong, maxInputFrames);

            var mono = new float[inputFrames];
            byte[] raw = new byte[inputFrames * frameBytes];
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
            int maxOutputFrames = (int)Math.Floor(format.SampleRate * MaxReplacementDurationSeconds);
            outputFrames = Math.Min(outputFrames, maxOutputFrames);
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
