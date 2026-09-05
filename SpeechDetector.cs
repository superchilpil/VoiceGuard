using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoiceGuard;

public sealed class SpeechDetector : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly List<byte> pcm48k = new();
    private readonly HashSet<string> blocked;
    private readonly Action<string> log;

    private readonly List<RecognitionWindow> workQueue = new();
    private readonly object workQueueSync = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource cts = new();
    private readonly Task worker;
    private int activeRecognitions;
    private long completedThroughBits;

    // Continuous analysis watermark.  Unlike the old max-completed timestamp,
    // this watermark only advances when every Whisper window belonging to a
    // completed PTT segment has finished.  Gaps between PTT segments are safe
    // automatically, so the output clock can cross them without being reset.
    private long nextSegmentId;
    private int nextCandidateEventId;
    private long activeSegmentId;
    private bool pttSegmentActive;
    private double activeSegmentStartSeconds;
    private double activeSegmentEndSeconds;
    private double analysisSafeThroughSeconds;
    private readonly Dictionary<long, int> segmentPending = new();
    private readonly Dictionary<long, double> segmentEnds = new();

    private WhisperFactory? factory;
    private WhisperProcessor? processor;

    private const int SampleRate = 48000;
    private const int BytesPerSecond = SampleRate * 2;

    // Short overlapping windows reduce the time between a spoken trigger and
    // the recognition result.  The 3-second output buffer provides the
    // protection window; analysis is allowed to run continuously inside it.
    private const double WindowSeconds = 0.75;
    private const int WindowBytes = (int)(BytesPerSecond * WindowSeconds);

    // Advance every 250 ms.  This gives each word multiple chances to appear
    // inside a complete recognition window without building the old 0.5 s
    // cadence backlog.
    private const double StepSeconds = 0.25;
    private const int StepBytes = (int)(BytesPerSecond * StepSeconds);

    public bool IsReady => processor != null;

    public double CompletedThroughSeconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref completedThroughBits));

    public bool HasPendingAnalysis
    {
        get { lock (workQueueSync) return activeRecognitions > 0 || workQueue.Count > 0; }
    }
    public double AnalysisSafeThroughSeconds => Volatile.Read(ref analysisSafeThroughBits) == 0 ? 0.0 : BitConverter.Int64BitsToDouble(Volatile.Read(ref analysisSafeThroughBits));
    private long analysisSafeThroughBits;

    private Action<double, double, string>? censorRequested;
    private Func<double>? outputCursorSeconds;

    public SpeechDetector(IEnumerable<string> words, Action<string> log, Func<double>? outputCursorSeconds = null)
    {
        blocked = new HashSet<string>(
            words.Where(x => !string.IsNullOrWhiteSpace(x))
                 .Select(Normalize),
            StringComparer.OrdinalIgnoreCase);

        this.log = log;
        this.outputCursorSeconds = outputCursorSeconds;
        worker = Task.Run(ProcessQueueAsync);
    }

    public void SetCensorCallback(Action<double, double, string>? callback)
    {
        censorRequested = callback;
    }

    public void SetOutputCursorProvider(Func<double>? provider)
    {
        // Provider is normally set once, after AudioEngine construction.
        outputCursorSeconds = provider;
    }

    public void SetWords(IEnumerable<string> words)
    {
        lock (sync)
        {
            blocked.Clear();

            foreach (var word in words)
            {
                var n = Normalize(word);
                if (!string.IsNullOrWhiteSpace(n))
                    blocked.Add(n);
            }
        }
    }

    private static string GetModelDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceGuard",
            "Models");
    }

    public async Task DownloadModelAsync(IProgress<double>? progress = null)
    {
        var models = GetModelDirectory();
        Directory.CreateDirectory(models);

        var modelPath = Path.Combine(models, "ggml-base.en.bin");

        // Whisper base.en is roughly 466 MB. Treat obviously incomplete
        // downloads as invalid rather than attempting to initialize them.
        const long minimumExpectedBytes = 110L * 1024 * 1024;

        if (File.Exists(modelPath))
        {
            var length = new FileInfo(modelPath).Length;
            if (length < minimumExpectedBytes)
            {
                log($"Existing base.en model is only {length / 1024 / 1024} MB; deleting incomplete file.");
                TryDelete(modelPath);
            }
        }

        if (!File.Exists(modelPath))
        {
            log("Downloading Whisper base.en model...");

            await using var stream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(GgmlType.BaseEn);

            await using var file = new FileStream(
                modelPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            var buffer = new byte[1024 * 128];
            long total = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                total += read;
                progress?.Report(total);
            }

            await file.FlushAsync();

            var finalLength = new FileInfo(modelPath).Length;
            if (finalLength < minimumExpectedBytes)
            {
                TryDelete(modelPath);
                throw new InvalidDataException(
                    $"small.en download appears incomplete ({finalLength / 1024 / 1024} MB).");
            }

            log($"Whisper base.en downloaded ({finalLength / 1024 / 1024} MB).");
        }

        await LoadModelAsync();
    }

    public async Task LoadModelAsync()
    {
        var modelPath = Path.Combine(
            GetModelDirectory(), "ggml-base.en.bin");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                "Whisper base.en model is not installed.", modelPath);

        var size = new FileInfo(modelPath).Length;
        if (size < 110L * 1024 * 1024)
            throw new InvalidDataException(
                $"base.en model is too small ({size / 1024 / 1024} MB). Delete Models and download it again.");

        factory?.Dispose();
        factory = WhisperFactory.FromPath(modelPath);

        processor?.Dispose();
        processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        log("Speech recognition READY — model: base.en");

        // Whisper's first inference can be dramatically slower than later
        // inferences because the native runtime initializes its model/runtime
        // state lazily. Warm it with silence before the user starts talking so
        // the first real PTT phrase does not pay that initialization cost.
        log("Whisper warm-up starting...");
        var warmupPcm = new byte[WindowBytes];
        using var warmupWav = Build16kMonoWav(warmupPcm);
        warmupWav.Position = 0;

        long warmupStarted = Environment.TickCount64;
        await foreach (var _ in processor.ProcessAsync(warmupWav))
        {
            // Intentionally discard any transcription from the silent warm-up.
        }

        double warmupMs = Environment.TickCount64 - warmupStarted;
        log($"Whisper warm-up complete — elapsed={warmupMs:0}ms. Ready for PTT.");
    }

    public void BeginPttSegment(double absoluteStartSeconds)
    {
        lock (sync)
        {
            if (pttSegmentActive)
                return;

            pttSegmentActive = true;
            activeSegmentId = ++nextSegmentId;
            activeSegmentStartSeconds = Math.Max(0, absoluteStartSeconds);

            // Everything between the previously completed PTT segment and this
            // segment's start contains no user speech that VoiceGuard analyzes.
            // It is therefore safe for the delayed output to cross.
            analysisSafeThroughSeconds = Math.Max(
                analysisSafeThroughSeconds,
                activeSegmentStartSeconds);
            Interlocked.Exchange(
                ref analysisSafeThroughBits,
                BitConverter.DoubleToInt64Bits(analysisSafeThroughSeconds));
            segmentPending[activeSegmentId] = 0;
        }
    }

    public void EndPttSegment(double absoluteEndSeconds)
    {
        lock (sync)
        {
            if (!pttSegmentActive)
                return;

            activeSegmentEndSeconds = Math.Max(activeSegmentStartSeconds, absoluteEndSeconds);
            pttSegmentActive = false;
            segmentEnds[activeSegmentId] = activeSegmentEndSeconds;

            // Flush the final partial window for this PTT segment.  It is tagged
            // with the segment so the watermark cannot advance until it finishes.
            if (pcm48k.Count >= BytesPerSecond / 5)
            {
                var finalWindow = pcm48k.ToArray();
                var finalOffset = _streamOffsetSeconds;
                var duration = finalWindow.Length / (double)BytesPerSecond;
                pcm48k.Clear();
                _streamOffsetSeconds = activeSegmentEndSeconds;
                EnqueueWindow(finalWindow, finalOffset, duration, activeSegmentId);
                log($"Flushed final speech window @ {finalOffset:0.000}s.");
            }
            else
            {
                pcm48k.Clear();
                _streamOffsetSeconds = activeSegmentEndSeconds;
            }

            TryAdvanceAnalysisWatermark_NoLock(activeSegmentId);
        }
    }

    private void EnqueueWindow(byte[] pcm, double absoluteStart, double duration, long segmentId)
    {
        lock (workQueueSync)
        {
            // Newest-first scheduling keeps the detector focused on audio that
            // is closest to the 3-second output boundary. Older overlapping
            // windows remain available for consensus/deduplication.
            workQueue.Add(new RecognitionWindow(pcm, absoluteStart, duration, segmentId));
        }
        segmentPending.TryGetValue(segmentId, out var pending);
        segmentPending[segmentId] = pending + 1;
        queueSignal.Release();
    }

    private void TryAdvanceAnalysisWatermark_NoLock(long segmentId)
    {
        if (!segmentEnds.TryGetValue(segmentId, out var end))
            return;

        if (segmentPending.TryGetValue(segmentId, out var pending) && pending > 0)
            return;

        analysisSafeThroughSeconds = Math.Max(analysisSafeThroughSeconds, end);
        Interlocked.Exchange(
            ref analysisSafeThroughBits,
            BitConverter.DoubleToInt64Bits(analysisSafeThroughSeconds));
        segmentEnds.Remove(segmentId);
        segmentPending.Remove(segmentId);
    }

    public void AddPcm48k(byte[] pcm, int count)
    {
        AddPcm48k(pcm, count, null);
    }

    // Adds a PTT audio packet using the AudioEngine's global capture timestamp.
    // This prevents the detector clock from collapsing to PTT-segment time when
    // there are gaps between presses.
    public void AddPcm48k(byte[] pcm, int count, double? absoluteStartSeconds)
    {
        if (!IsReady || count <= 0)
            return;

        lock (sync)
        {
            if (pcm48k.Count == 0 && absoluteStartSeconds.HasValue)
                _streamOffsetSeconds = Math.Max(0, absoluteStartSeconds.Value);

            pcm48k.AddRange(pcm.AsSpan(0, count).ToArray());

            while (pcm48k.Count >= WindowBytes)
            {
                var window = pcm48k.Take(WindowBytes).ToArray();
                EnqueueWindow(
                    window,
                    _streamOffsetSeconds,
                    window.Length / (double)BytesPerSecond,
                    pttSegmentActive ? activeSegmentId : 0);

                int remove = Math.Min(StepBytes, pcm48k.Count);
                pcm48k.RemoveRange(0, remove);
                _streamOffsetSeconds += remove / (double)BytesPerSecond;
            }
        }
    }

    private double _streamOffsetSeconds;

    public void Reset(double nextSegmentStartSeconds = 0)
    {
        byte[]? finalWindow = null;
        double finalOffset = 0;

        lock (sync)
        {
            // Flush a short final phrase instead of leaving it stranded until
            // the next PTT press. Its timestamp remains on the current global
            // PCM timeline.
            if (pcm48k.Count >= BytesPerSecond / 5)
            {
                finalWindow = pcm48k.ToArray();
                finalOffset = _streamOffsetSeconds;
            }

            pcm48k.Clear();
            _streamOffsetSeconds = Math.Max(0, nextSegmentStartSeconds);
        }

        if (finalWindow != null)
        {
            long segmentId;
            lock (sync)
                segmentId = pttSegmentActive ? activeSegmentId : 0;

            lock (sync)
                EnqueueWindow(
                    finalWindow,
                    finalOffset,
                    finalWindow.Length / (double)BytesPerSecond,
                    segmentId);

            log($"Flushed final speech window @ {finalOffset:0.000}s.");
        }

        // Already queued recognition work is intentionally preserved.
    }

    private async Task ProcessQueueAsync()
    {
        log($"Stage 6 recognition worker started. Model=base.en Window={WindowSeconds:0.00}s Step={StepSeconds:0.00}s.");

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await queueSignal.WaitAsync(cts.Token);

                while (true)
                {
                    RecognitionWindow? window = null;
                    int remaining;

                    lock (workQueueSync)
                    {
                        if (workQueue.Count == 0)
                            break;

                        int newestIndex = 0;
                        double newestStart = workQueue[0].AbsoluteStart;

                        for (int i = 1; i < workQueue.Count; i++)
                        {
                            if (workQueue[i].AbsoluteStart > newestStart)
                            {
                                newestStart = workQueue[i].AbsoluteStart;
                                newestIndex = i;
                            }
                        }

                        window = workQueue[newestIndex];
                        workQueue.RemoveAt(newestIndex);
                        remaining = workQueue.Count;
                    }

                    log($"ASR DISPATCH — source={window.AbsoluteStart:0.000}s | queueRemaining={remaining} | priority=newest");
                    await TranscribeWindowAsync(window, remaining);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log("Speech worker error: " + ex.Message);
        }
    }

    private async Task TranscribeWindowAsync(RecognitionWindow window, int queueDepthAtDispatch)
    {
        if (processor == null)
            return;

        try
        {
            Interlocked.Increment(ref activeRecognitions);
            long recognitionStarted = Environment.TickCount64;
            int queueDepthAtStart = queueDepthAtDispatch;
            using var wav = Build16kMonoWav(window.Pcm48k);
            wav.Position = 0;

            lock (audioWindowSync)
            {
                recentAudioWindows.Add(window);
                double cutoff = window.AbsoluteStart - 8.0;
                recentAudioWindows.RemoveAll(x => x.AbsoluteStart + x.Duration < cutoff);
            }

            var vad = AnalyzeSpeechActivity(window.Pcm48k);

            if (!vad.IsSpeech)
            {
                log(
                    $"VAD @ {window.AbsoluteStart:0.000}s: " +
                    $"LOW ACTIVITY — Whisper skipped | " +
                    $"activity={vad.Activity:P0} | " +
                    $"peak={vad.Peak:0.000}");
                return;
            }

            log(
                $"VAD @ {window.AbsoluteStart:0.000}s: SPEECH | " +
                $"activity={vad.Activity:P0} | peak={vad.Peak:0.000}");

            var recognized = new List<string>();

            await foreach (var result in processor.ProcessAsync(wav))
            {
                var phrase = (result.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(phrase))
                    recognized.Add(phrase);
            }

            double recognitionMs = Environment.TickCount64 - recognitionStarted;
            log($"ASR COMPLETE @ {window.AbsoluteStart:0.000}s | elapsed={recognitionMs:0}ms | audio={window.Duration:0.000}s | queueRemaining={queueDepthAtStart}");
            double detectionEnd = window.AbsoluteStart + window.Duration;
            double outputCursor = outputCursorSeconds?.Invoke() ?? 0.0;
            double headroom = detectionEnd - outputCursor;
            log($"DETECTION HEADROOM — sourceEnd={detectionEnd:0.000}s | outputCursor={outputCursor:0.000}s | headroom={headroom:0.000}s | queueRemaining={queueDepthAtStart}");

            if (recognized.Count == 0)
            {
                log($"Recognition window @ {window.AbsoluteStart:0.000}s produced no speech.");
                return;
            }

            foreach (var phrase in recognized)
            {
                log(
                    $"Speech GLOBAL @ {window.AbsoluteStart:0.000}s " +
                    $"({window.Duration:0.000}s audio): {phrase}");

                CheckForCandidates(phrase, window);
            }
        }
        catch (Exception ex)
        {
            log("Speech recognition error: " + ex.Message);
        }
        finally
        {
            // A window is considered safe only after VAD/Whisper processing has
            // completely finished. The audio engine uses this watermark to keep
            // the delayed output from outrunning analysis.
            double completed = window.AbsoluteStart + window.Duration;
            long bits = BitConverter.DoubleToInt64Bits(completed);
            long prior = Interlocked.Read(ref completedThroughBits);
            while (completed > BitConverter.Int64BitsToDouble(prior))
            {
                long observed = Interlocked.CompareExchange(ref completedThroughBits, bits, prior);
                if (observed == prior) break;
                prior = observed;
            }
            Interlocked.Decrement(ref activeRecognitions);

            lock (sync)
            {
                if (segmentPending.TryGetValue(window.SegmentId, out var pending))
                {
                    segmentPending[window.SegmentId] = Math.Max(0, pending - 1);
                    TryAdvanceAnalysisWatermark_NoLock(window.SegmentId);
                }
            }
        }
    }


    private VadResult AnalyzeSpeechActivity(byte[] pcm)
    {
        // 10 ms frames at 48 kHz, 16-bit mono PCM.
        const int frameSamples = 480;
        const int frameBytes = frameSamples * 2;

        if (pcm.Length < frameBytes)
            return new VadResult(false, 0, 0);

        int frames = pcm.Length / frameBytes;
        int activeFrames = 0;
        double peak = 0;
        double sumEnergy = 0;

        var frameEnergy = new double[frames];

        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * frameBytes;
            double sum = 0;

            for (int i = 0; i < frameSamples; i++)
            {
                short sample = BitConverter.ToInt16(pcm, offset + i * 2);
                double value = sample / 32768.0;
                sum += value * value;
            }

            double rms = Math.Sqrt(sum / frameSamples);
            frameEnergy[frame] = rms;
            sumEnergy += rms;
            peak = Math.Max(peak, rms);
        }

        // Adaptive threshold: prevents normal microphone noise from being
        // treated as speech while still handling different mic gain levels.
        double average = sumEnergy / frames;
        double threshold = Math.Max(0.012, Math.Max(average * 2.2, peak * 0.16));

        for (int i = 0; i < frameEnergy.Length; i++)
        {
            if (frameEnergy[i] >= threshold)
                activeFrames++;
        }

        double activity = activeFrames / (double)frames;

        // A 1-second window needs meaningful speech activity, not one isolated
        // noise spike. These values are deliberately conservative.
        bool isSpeech =
            peak >= 0.025 &&
            activity >= 0.12;

        return new VadResult(isSpeech, activity, peak);
    }

    private sealed record VadResult(
        bool IsSpeech,
        double Activity,
        double Peak);


    private bool MatchesBlockedWordOrAlias(string phrase, string blockedWord)
    {
        if (ContainsWholeWord(phrase, blockedWord))
            return true;

        if (!blockedAliases.TryGetValue(blockedWord, out var aliases))
            return false;

        return aliases.Any(alias => ContainsWholeWord(phrase, alias));
    }

    // Adds a user-defined Whisper transcription that should be treated as an
    // alias for a configured blocked word.
    public void AddBlockedWordAlias(string blockedWord, string alias)
    {
        blockedWord = Normalize(blockedWord);
        alias = Normalize(alias);

        if (string.IsNullOrWhiteSpace(blockedWord) ||
            string.IsNullOrWhiteSpace(alias))
            return;

        lock (sync)
        {
            if (!blocked.Contains(blockedWord))
                blocked.Add(blockedWord);

            if (!blockedAliases.TryGetValue(blockedWord, out var aliases))
            {
                aliases = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                blockedAliases[blockedWord] = aliases;
            }

            aliases.Add(alias);
        }

        log($"Added transcription alias: \"{alias}\" → \"{blockedWord}\"");
    }

    public void ClearBlockedWordAliases()
    {
        lock (sync)
        {
            blockedAliases.Clear();
        }
    }

    public void ClearBlockedWordAliasesFor(string blockedWord)
    {
        blockedWord = Normalize(blockedWord);
        lock (sync) blockedAliases.Remove(blockedWord);
    }

    public IReadOnlyCollection<string> GetBlockedWordAliases(string blockedWord)
    {
        blockedWord = Normalize(blockedWord);

        lock (sync)
        {
            if (!blockedAliases.TryGetValue(blockedWord, out var aliases))
                return Array.Empty<string>();

            return aliases.ToArray();
        }
    }

    private void CheckForCandidates(string phrase, RecognitionWindow window)
    {
        List<string> matches;

        lock (sync)
        {
            matches = blocked
                .Where(w => MatchesBlockedWordOrAlias(phrase, w))
                .ToList();
        }

        var acoustic = AnalyzeWindowAudio(window);

        foreach (var match in matches)
        {
            var occurrences = FindWordOccurrences(phrase, match);

            // A Whisper phrase can contain the same blocked word more than
            // once (for example: "bitch, bitch"). The old detector only
            // examined the first textual occurrence, so the first/last word
            // could be missed while the middle occurrence was censored.
            // Build one observation per occurrence and map that occurrence
            // onto the acoustic speech region.
            if (occurrences.Count > 1)
            {
                log(
                    $"MULTI-OCCURRENCE: {match} | occurrences={occurrences.Count} | " +
                    $"phrase=\"{phrase}\" | window={window.AbsoluteStart:0.000}s→" +
                    $"{window.AbsoluteStart + window.Duration:0.000}s");
            }

            for (int occurrenceIndex = 0; occurrenceIndex < occurrences.Count; occurrenceIndex++)
            {
                var occurrence = occurrences[occurrenceIndex];
                double regionStart = acoustic.Start;
                double regionEnd = acoustic.End;

                // If the energy detector produced a compact region, use it as
                // the speech envelope. Otherwise fall back to the recognition
                // window so multiple words can still be positioned from their
                // textual fractions.
                if (acoustic.Duration > 0.70)
                {
                    regionStart = window.AbsoluteStart;
                    regionEnd = window.AbsoluteStart + window.Duration;
                }

                double regionDuration = Math.Max(0.001, regionEnd - regionStart);
                double occurrenceStart = regionStart + regionDuration * occurrence.StartFraction;
                double occurrenceEnd = regionStart + regionDuration * occurrence.EndFraction;

                // Whisper token boundaries are not acoustic boundaries. Give
                // very short occurrences a small realistic envelope while
                // staying inside the available speech/window region.
                double occurrenceDuration = occurrenceEnd - occurrenceStart;
                if (occurrenceDuration < 0.12)
                {
                    double center = (occurrenceStart + occurrenceEnd) / 2.0;
                    double half = 0.06;
                    occurrenceStart = Math.Max(regionStart, center - half);
                    occurrenceEnd = Math.Min(regionEnd, center + half);
                }

                double acousticDuration = Math.Max(0, occurrenceEnd - occurrenceStart);
                bool compact = acousticDuration >= 0.08 && acousticDuration <= 0.70;

                var observation = new CandidateObservation(
                    match,
                    window.AbsoluteStart,
                    window.AbsoluteStart + window.Duration,
                    phrase,
                    occurrence.StartFraction,
                    occurrence.EndFraction,
                    occurrenceStart,
                    occurrenceEnd,
                    acousticDuration,
                    compact,
                    occurrenceIndex,
                    occurrences.Count);

                AddObservation(observation);
            }
        }
    }

    private const double OutputDelaySeconds = 3.0;

    // Optional transcription aliases. These map common Whisper
    // misrecognitions to a configured blocked word.
    private readonly Dictionary<string, HashSet<string>> blockedAliases =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object candidateSync = new();
    private readonly List<RecognitionWindow> recentAudioWindows = new();
    private readonly object audioWindowSync = new();
    private readonly List<CandidateCluster> candidateClusters = new();

    private void AddObservation(CandidateObservation observation)
    {
        CandidateCluster? cluster;
        double observationCenter = (observation.AcousticStart + observation.AcousticEnd) / 2.0;

        lock (candidateSync)
        {
            // Associate overlapping Whisper windows with the same spoken event,
            // but do NOT merge adjacent repetitions of the same word. The old
            // interval-overlap rule could collapse "bitch bitch bitch" into a
            // single cluster because all three short acoustic regions touched
            // one another.
            cluster = candidateClusters
                .Where(c => string.Equals(c.Word, observation.Word,
                    StringComparison.OrdinalIgnoreCase))
                .Where(c => Math.Abs(c.CenterSeconds - observationCenter) <= 0.22)
                .OrderBy(c => Math.Abs(c.CenterSeconds - observationCenter))
                .FirstOrDefault();

            if (cluster == null)
            {
                cluster = new CandidateCluster(++nextCandidateEventId, observation);
                candidateClusters.Add(cluster);
                log(
                    $"CANDIDATE EVENT CREATED — #{cluster.EventId} {cluster.Word} | " +
                    $"center={observationCenter:0.000}s | occurrence={observation.OccurrenceIndex + 1}/{observation.OccurrenceCount}");
            }
            else
            {
                cluster.Add(observation);
            }

            double cutoff = observation.WindowEnd - 8.0;
            candidateClusters.RemoveAll(c => c.AcousticEnd < cutoff);
        }

        ReportConsensus(cluster);
    }

    private void ReportConsensus(CandidateCluster cluster)
    {
        var compact = cluster.Observations
            .Where(x => x.CompactAcoustic)
            .OrderBy(x => x.AcousticStart)
            .ToList();

        double confidence = CalculateConfidence(cluster);

        if (compact.Count == 0)
        {
            log(
                $"CENSOR PREVIEW: {cluster.Word} | " +
                $"confidence={confidence:P0} | " +
                $"observations={cluster.Observations.Count} | " +
                $"compact=0 | action=WAIT");
            return;
        }

        // Consensus is the interval covered by the strongest overlapping
        // compact observations. We intentionally do not average endpoints.
        var consensus = FindConsensusInterval(compact);

        if (consensus == null)
        {
            log(
                $"CENSOR PREVIEW: {cluster.Word} | " +
                $"confidence={confidence:P0} | " +
                $"observations={cluster.Observations.Count} | " +
                $"compact={compact.Count} | " +
                $"acoustic_interval=UNAVAILABLE | action=WAIT");
            return;
        }

        double start = consensus.Value.Start;
        double end = consensus.Value.End;
        double duration = end - start;

        // A real censor interval must be word-sized and supported by at least
        // At least one compact observation is sufficient once the confidence threshold is met.
        // The delayed output buffer provides the safety window for late recognition.
        bool usable = duration >= 0.08 &&
                      duration <= 0.50 &&
                      compact.Count >= 1 &&
                      confidence >= 0.40;

        double delayedStart = start + OutputDelaySeconds;
        double delayedEnd = end + OutputDelaySeconds;

        log(
            $"CONSENSUS: {cluster.Word} | " +
            $"confidence={confidence:P0} | " +
            $"observations={cluster.Observations.Count} | " +
            $"compact={compact.Count} | " +
            $"consensus PCM={start:0.000}s→{end:0.000}s | " +
            $"support={compact.Count} | " +
            $"duration={duration * 1000:0}ms | " +
            $"delayed output={delayedStart:0.000}s→{delayedEnd:0.000}s | " +
            $"action={(usable ? "CENSOR" : "WAIT")}");

        if (usable)
        {
            try
            {
                if (censorRequested == null)
                {
                    log($"CENSOR CALLBACK MISSING — {cluster.Word} {start:0.000}s→{end:0.000}s");
                }
                else
                {
                    censorRequested.Invoke(start, end, cluster.Word);
                }
            }
            catch (Exception ex)
            {
                log($"Censor callback error: {ex.Message}");
            }
        }
    }

    private static (double Start, double End)? FindConsensusInterval(
        List<CandidateObservation> observations)
    {
        if (observations.Count == 0)
            return null;

        // Whisper recognition is already the semantic signal. The acoustic
        // estimate is used to place the censor, but we no longer require
        // several windows to independently pass the compact-acoustic test.
        // That was preventing real detections: repeated "bitch" detections
        // were reaching compact=2 while every preview still said WAIT.
        var valid = observations
            .Select(x => new
            {
                Center = (x.AcousticStart + x.AcousticEnd) / 2.0,
                Duration = x.AcousticEnd - x.AcousticStart,
                x.AcousticStart,
                x.AcousticEnd
            })
            .Where(x => x.Duration >= 0.05 && x.Duration <= 1.00)
            .OrderByDescending(x => x.Duration <= 0.50 ? 1 : 0)
            .ThenBy(x => x.Duration)
            .ToList();

        if (valid.Count == 0)
            return null;

        // Prefer the shortest valid acoustic estimate. This keeps the
        // replacement conservative when Whisper's 1-second window produces
        // a broad speech estimate.
        var chosen = valid.First();

        double duration = Math.Clamp(chosen.Duration, 0.08, 0.50);
        double center = chosen.Center;

        return (center - duration / 2.0, center + duration / 2.0);
    }

    private double CalculateConfidence(CandidateCluster cluster)
    {
        int observations = cluster.Observations.Count;
        int positiveAcoustic = cluster.Observations.Count(x => x.CompactAcoustic);

        // Repeated Whisper matches are strong evidence. Acoustic support adds
        // a small bonus but is not allowed to make otherwise repeated,
        // correctly recognized words invisible to the censor.
        double confidence = 0.40 +
            Math.Min(0.50, Math.Max(0, observations - 1) * 0.15);

        if (positiveAcoustic >= 2)
            confidence += 0.05;

        int distinctPhrases = cluster.Observations
            .Select(x => Normalize(x.Phrase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctPhrases >= 2)
            confidence += 0.05;

        return Math.Clamp(confidence, 0, 1);
    }

    private AcousticEstimate AnalyzeWindowAudio(RecognitionWindow window)
    {
        const int frameSamples = 480; // 10 ms @ 48 kHz
        const int frameBytes = frameSamples * 2;
        var pcm = window.Pcm48k;

        if (pcm.Length < frameBytes)
            return new AcousticEstimate(
                window.AbsoluteStart,
                window.AbsoluteStart + window.Duration);

        var energy = new double[pcm.Length / frameBytes];

        for (int frame = 0; frame < energy.Length; frame++)
        {
            int offset = frame * frameBytes;
            double sum = 0;

            for (int i = 0; i < frameSamples; i++)
            {
                short sample = BitConverter.ToInt16(pcm, offset + i * 2);
                double v = sample / 32768.0;
                sum += v * v;
            }

            energy[frame] = Math.Sqrt(sum / frameSamples);
        }

        double peak = energy.Max();
        if (peak <= 0.0001)
            return new AcousticEstimate(
                window.AbsoluteStart,
                window.AbsoluteStart + window.Duration);

        // Use a slightly stricter threshold than Stage 4.7 and require a
        // compact region. This intentionally rejects whole-window speech.
        double threshold = Math.Max(0.012, peak * 0.30);

        int first = 0;
        while (first < energy.Length && energy[first] < threshold)
            first++;

        int last = energy.Length - 1;
        while (last >= first && energy[last] < threshold)
            last--;

        if (first > last)
            return new AcousticEstimate(
                window.AbsoluteStart,
                window.AbsoluteStart + window.Duration);

        double start = window.AbsoluteStart + first * 0.010;
        double end = window.AbsoluteStart + (last + 1) * 0.010;

        return new AcousticEstimate(start, end);
    }

    private List<WordOccurrence> FindWordOccurrences(string phrase, string blockedWord)
    {
        var terms = new List<string> { blockedWord };

        if (blockedAliases.TryGetValue(blockedWord, out var aliases))
            terms.AddRange(aliases);

        var occurrences = new List<WordOccurrence>();

        foreach (var term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (Match match in Regex.Matches(
                phrase,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (phrase.Length == 0)
                    continue;

                occurrences.Add(new WordOccurrence(
                    match.Index / (double)phrase.Length,
                    (match.Index + match.Length) / (double)phrase.Length));
            }
        }

        // Sort by where the occurrence appears in the transcription and
        // collapse aliases that happen to point at the same text range.
        return occurrences
            .OrderBy(x => x.StartFraction)
            .ThenBy(x => x.EndFraction)
            .GroupBy(x => Math.Round(x.StartFraction, 4))
            .Select(g => g.OrderByDescending(x => x.EndFraction - x.StartFraction).First())
            .ToList();
    }

    private sealed class CandidateCluster
    {
        public int EventId { get; }
        public string Word { get; }
        public double AcousticStart { get; private set; }
        public double AcousticEnd { get; private set; }
        public double CenterSeconds => (AcousticStart + AcousticEnd) / 2.0;
        public List<CandidateObservation> Observations { get; } = new();

        public CandidateCluster(int eventId, CandidateObservation observation)
        {
            EventId = eventId;
            Word = observation.Word;
            AcousticStart = observation.AcousticStart;
            AcousticEnd = observation.AcousticEnd;
            Observations.Add(observation);
        }

        public void Add(CandidateObservation observation)
        {
            AcousticStart = Math.Min(AcousticStart, observation.AcousticStart);
            AcousticEnd = Math.Max(AcousticEnd, observation.AcousticEnd);
            Observations.Add(observation);
        }
    }

    private sealed record CandidateObservation(
        string Word,
        double WindowStart,
        double WindowEnd,
        string Phrase,
        double StartFraction,
        double EndFraction,
        double AcousticStart,
        double AcousticEnd,
        double AcousticDuration,
        bool CompactAcoustic,
        int OccurrenceIndex,
        int OccurrenceCount);

    private sealed record AcousticEstimate(double Start, double End)
    {
        public double Duration => Math.Max(0, End - Start);
    }

    private sealed record WordOccurrence(
        double StartFraction,
        double EndFraction);

    private sealed record RecognitionSegment(
        double Start,
        double End,
        string Text);

    private static bool ContainsWholeWord(string text, string word)
    {
        return Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(
            value.Trim().ToLowerInvariant(),
            @"[^\p{L}\p{N}]+",
            " ").Trim();
    }

    private static MemoryStream Build16kMonoWav(byte[] pcm48k)
    {
        int sourceSamples = pcm48k.Length / 2;
        int targetSamples = sourceSamples / 3;
        byte[] targetPcm = new byte[targetSamples * 2];

        for (int i = 0; i < targetSamples; i++)
        {
            int sourceIndex = i * 3 * 2;
            targetPcm[i * 2] = pcm48k[sourceIndex];
            targetPcm[i * 2 + 1] = pcm48k[sourceIndex + 1];
        }

        var wav = new MemoryStream(capacity: 44 + targetPcm.Length);

        using (var writer = new BinaryWriter(
            wav,
            Encoding.UTF8,
            leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + targetPcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);

            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(targetPcm.Length);
            writer.Write(targetPcm);
            writer.Flush();
        }

        wav.Position = 0;
        return wav;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();

        try
        {
            queueSignal.Release();
        }
        catch
        {
        }

        try
        {
            await worker;
        }
        catch
        {
        }

        queueSignal.Dispose();
        cts.Dispose();

        processor?.Dispose();
        processor = null;

        factory?.Dispose();
        factory = null;
    }

    private sealed record RecognitionWindow(byte[] Pcm48k, double AbsoluteStart, double Duration, long SegmentId);
}
