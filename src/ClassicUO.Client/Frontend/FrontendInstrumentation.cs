using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Frontend;

internal sealed class FrontendInstrumentation : IDisposable
{
    private const int ReportVersion = 1;
    private static readonly long InitialCheckpointDelay = Stopwatch.Frequency * 2;
    private static readonly long CheckpointInterval = Stopwatch.Frequency * 5;
    private static FrontendInstrumentation _current;

    private readonly string _path;
    private readonly FrontendOptions _options;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly bool _profilerWasEnabled;
    private readonly bool _profilerWasLoggingSpikes;
    private readonly Dictionary<string, HotspotAccumulator> _hotspots = new(StringComparer.Ordinal);

    private long _nextCheckpointAt;
    private long _updateCount;
    private long _drawCount;
    private long _encodedFrameCount;
    private long _enqueuedFrameCount;
    private long _droppedFrameCount;
    private long _encodedBytes;
    private long _totalSprites;
    private long _totalFlushes;
    private long _totalTextureSwitches;
    private double _totalUpdateMilliseconds;
    private double _maxUpdateMilliseconds;
    private double _totalDrawMilliseconds;
    private double _maxDrawMilliseconds;
    private double _totalCaptureMilliseconds;
    private double _maxCaptureMilliseconds;
    private int _maxSprites;
    private int _maxFlushes;
    private int _maxTextureSwitches;
    private int _maxRenderedObjects;
    private bool _attached;
    private bool _disposed;
    private bool _reportedWriteFailure;
    private bool _profilerFrameStarted;

    private FrontendInstrumentation(FrontendOptions options)
    {
        _options = options;
        _path = options.InstrumentationPath;

        if (_current != null)
        {
            throw new InvalidOperationException("Only one frontend instrumentation session can be active.");
        }

        string directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _profilerWasEnabled = Profiler.Enabled;
        _profilerWasLoggingSpikes = Profiler.LogSpikes;
        Profiler.Enabled = true;
        Profiler.LogSpikes = false;
        Profiler.Reset();
        _nextCheckpointAt = _startedTimestamp + InitialCheckpointDelay;
        _current = this;

        Log.Info($"Frontend instrumentation will write to '{_path}'.");
    }

    public static bool IsEnabled => _current != null;

    public static FrontendInstrumentation Create(FrontendOptions options) =>
        string.IsNullOrWhiteSpace(options?.InstrumentationPath)
            ? null
            : new FrontendInstrumentation(options);

    public long BeginSample() => Stopwatch.GetTimestamp();

    public void BeginProfilerFrame()
    {
        if (_profilerFrameStarted)
        {
            Profiler.EndFrame();
        }

        Profiler.BeginFrame();
        _profilerFrameStarted = true;
    }

    public void RecordUpdate(long started, bool attached)
    {
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _updateCount++;
        _totalUpdateMilliseconds += milliseconds;
        _maxUpdateMilliseconds = Math.Max(_maxUpdateMilliseconds, milliseconds);
        _attached = attached;
        CheckpointIfDue();
    }

    public void RecordDraw(
        long started,
        in FrontendPresentResult present,
        int sprites,
        int flushes,
        int textureSwitches,
        int renderedObjects,
        bool attached
    )
    {
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _drawCount++;
        _totalDrawMilliseconds += milliseconds;
        _maxDrawMilliseconds = Math.Max(_maxDrawMilliseconds, milliseconds);
        _totalSprites += sprites;
        _totalFlushes += flushes;
        _totalTextureSwitches += textureSwitches;
        _maxSprites = Math.Max(_maxSprites, sprites);
        _maxFlushes = Math.Max(_maxFlushes, flushes);
        _maxTextureSwitches = Math.Max(_maxTextureSwitches, textureSwitches);
        _maxRenderedObjects = Math.Max(_maxRenderedObjects, renderedObjects);
        _attached = attached;

        if (present.EncodedBytes > 0)
        {
            _encodedFrameCount++;
            _encodedBytes += present.EncodedBytes;
            _totalCaptureMilliseconds += present.CaptureMilliseconds;
            _maxCaptureMilliseconds = Math.Max(
                _maxCaptureMilliseconds,
                present.CaptureMilliseconds
            );
        }

        if (present.Enqueued)
        {
            _enqueuedFrameCount++;
        }

        if (present.Dropped)
        {
            _droppedFrameCount++;
        }
    }

    public static HotspotScope Measure(string category, string name) =>
        new(_current, category, name);

    private void RecordHotspot(string category, string name, long started)
    {
        string key = category + '\0' + name;

        if (!_hotspots.TryGetValue(key, out HotspotAccumulator value))
        {
            value = new HotspotAccumulator(category, name);
            _hotspots.Add(key, value);
        }

        value.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void CheckpointIfDue()
    {
        long now = Stopwatch.GetTimestamp();

        if (now < _nextCheckpointAt)
        {
            return;
        }

        WriteReport(completed: false);
        _nextCheckpointAt = now + CheckpointInterval;
    }

    internal void WriteReport(bool completed)
    {
        try
        {
            FrontendInstrumentationReport report = CreateReport(completed);
            string temporaryPath = _path + ".tmp";

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None
                   ))
            {
                JsonSerializer.Serialize(
                    stream,
                    report,
                    FrontendJsonContext.Default.FrontendInstrumentationReport
                );
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            _reportedWriteFailure = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_reportedWriteFailure)
            {
                Log.Warn($"Unable to write frontend instrumentation report '{_path}': {ex.Message}");
                _reportedWriteFailure = true;
            }
        }
    }

    private FrontendInstrumentationReport CreateReport(bool completed)
    {
        FrontendInstrumentationMetric[] profilerContexts = Profiler.AllFrameData
            .Where(data => data.Context is { Length: > 0 })
            .Select(data => new FrontendInstrumentationMetric
            {
                Name = string.Join(" > ", data.Context),
                AverageMilliseconds = data.AverageTime,
                LastMilliseconds = data.LastTime,
                PeakMilliseconds = data.PeakTime
            })
            .OrderByDescending(data => data.AverageMilliseconds)
            .ThenByDescending(data => data.PeakMilliseconds)
            .ToArray();

        FrontendInstrumentationHotspot[] hotspots = _hotspots.Values
            .Select(value => new FrontendInstrumentationHotspot
            {
                Category = value.Category,
                Name = value.Name,
                Samples = value.Samples,
                TotalMilliseconds = value.TotalMilliseconds,
                AverageMilliseconds = value.AverageMilliseconds,
                PeakMilliseconds = value.PeakMilliseconds
            })
            .OrderByDescending(value => value.TotalMilliseconds)
            .ThenByDescending(value => value.PeakMilliseconds)
            .ToArray();

        return new FrontendInstrumentationReport
        {
            ReportVersion = ReportVersion,
            Mode = _options.Mode.ToString(),
            FrameFormat = _options.FrameFormat.ToString(),
            TargetFramesPerSecond = _options.FramesPerSecond,
            StartedUtc = _startedUtc.ToString("O", CultureInfo.InvariantCulture),
            CapturedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SessionSeconds = Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds,
            Completed = completed,
            Attached = _attached,
            Totals = new FrontendInstrumentationTotals
            {
                Updates = _updateCount,
                Draws = _drawCount,
                EncodedFrames = _encodedFrameCount,
                EnqueuedFrames = _enqueuedFrameCount,
                DroppedFrames = _droppedFrameCount,
                EncodedBytes = _encodedBytes,
                AverageUpdateMilliseconds = Average(_totalUpdateMilliseconds, _updateCount),
                PeakUpdateMilliseconds = _maxUpdateMilliseconds,
                AverageDrawMilliseconds = Average(_totalDrawMilliseconds, _drawCount),
                PeakDrawMilliseconds = _maxDrawMilliseconds,
                AverageCaptureMilliseconds = Average(
                    _totalCaptureMilliseconds,
                    _encodedFrameCount
                ),
                PeakCaptureMilliseconds = _maxCaptureMilliseconds,
                AverageSpritesPerDraw = Average(_totalSprites, _drawCount),
                PeakSpritesPerDraw = _maxSprites,
                AverageFlushesPerDraw = Average(_totalFlushes, _drawCount),
                PeakFlushesPerDraw = _maxFlushes,
                AverageTextureSwitchesPerDraw = Average(
                    _totalTextureSwitches,
                    _drawCount
                ),
                PeakTextureSwitchesPerDraw = _maxTextureSwitches,
                PeakRenderedObjects = _maxRenderedObjects
            },
            ProfilerContexts = profilerContexts,
            Hotspots = hotspots
        };
    }

    private static double Average(double total, long count) => count == 0 ? 0 : total / count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_profilerFrameStarted)
        {
            Profiler.EndFrame();
            Profiler.BeginFrame();
            _profilerFrameStarted = false;
        }

        WriteReport(completed: true);

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }

        if (!_profilerWasEnabled)
        {
            Profiler.Enabled = false;
        }

        Profiler.LogSpikes = _profilerWasLoggingSpikes;
    }

    internal readonly struct HotspotScope : IDisposable
    {
        private readonly FrontendInstrumentation _owner;
        private readonly string _category;
        private readonly string _name;
        private readonly long _started;

        public HotspotScope(FrontendInstrumentation owner, string category, string name)
        {
            _owner = owner;
            _category = category;
            _name = name;
            _started = owner == null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            _owner?.RecordHotspot(_category, _name, _started);
        }
    }

    private sealed class HotspotAccumulator
    {
        public HotspotAccumulator(string category, string name)
        {
            Category = category;
            Name = name;
        }

        public string Category { get; }
        public string Name { get; }
        public long Samples { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public double PeakMilliseconds { get; private set; }
        public double AverageMilliseconds => Average(TotalMilliseconds, Samples);

        public void Record(double milliseconds)
        {
            Samples++;
            TotalMilliseconds += milliseconds;
            PeakMilliseconds = Math.Max(PeakMilliseconds, milliseconds);
        }
    }
}

internal sealed class FrontendInstrumentationReport
{
    public int ReportVersion { get; set; }
    public string Mode { get; set; }
    public string FrameFormat { get; set; }
    public int TargetFramesPerSecond { get; set; }
    public string StartedUtc { get; set; }
    public string CapturedUtc { get; set; }
    public double SessionSeconds { get; set; }
    public bool Completed { get; set; }
    public bool Attached { get; set; }
    public FrontendInstrumentationTotals Totals { get; set; }
    public FrontendInstrumentationMetric[] ProfilerContexts { get; set; }
    public FrontendInstrumentationHotspot[] Hotspots { get; set; }
}

internal sealed class FrontendInstrumentationTotals
{
    public long Updates { get; set; }
    public long Draws { get; set; }
    public long EncodedFrames { get; set; }
    public long EnqueuedFrames { get; set; }
    public long DroppedFrames { get; set; }
    public long EncodedBytes { get; set; }
    public double AverageUpdateMilliseconds { get; set; }
    public double PeakUpdateMilliseconds { get; set; }
    public double AverageDrawMilliseconds { get; set; }
    public double PeakDrawMilliseconds { get; set; }
    public double AverageCaptureMilliseconds { get; set; }
    public double PeakCaptureMilliseconds { get; set; }
    public double AverageSpritesPerDraw { get; set; }
    public int PeakSpritesPerDraw { get; set; }
    public double AverageFlushesPerDraw { get; set; }
    public int PeakFlushesPerDraw { get; set; }
    public double AverageTextureSwitchesPerDraw { get; set; }
    public int PeakTextureSwitchesPerDraw { get; set; }
    public int PeakRenderedObjects { get; set; }
}

internal sealed class FrontendInstrumentationMetric
{
    public string Name { get; set; }
    public double AverageMilliseconds { get; set; }
    public double LastMilliseconds { get; set; }
    public double PeakMilliseconds { get; set; }
}

internal sealed class FrontendInstrumentationHotspot
{
    public string Category { get; set; }
    public string Name { get; set; }
    public long Samples { get; set; }
    public double TotalMilliseconds { get; set; }
    public double AverageMilliseconds { get; set; }
    public double PeakMilliseconds { get; set; }
}
