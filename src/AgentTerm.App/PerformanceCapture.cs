using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace AgentTerm_App;

internal enum PerformanceMetric { UiQueueDelay, OutputQueueDelay, CaptureLatency, FrameRefresh, RasterPreparation, InputDispatch }

internal static class PerformanceCapture
{
    private sealed class Distribution
    {
        private readonly object _gate = new();
        private long _count;
        private double _total, _maximum;
        private readonly long[] _buckets = new long[8];
        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _total += value;
                _maximum = Math.Max(_maximum, value);
                var index = 0;
                while (index < Bounds.Length && value > Bounds[index]) index++;
                _buckets[index]++;
            }
        }
        public object Take()
        {
            lock (_gate)
            {
                var result = new { count = _count, meanMs = _count == 0 ? 0 : _total / _count,
                    maxMs = _maximum, buckets = (long[])_buckets.Clone() };
                _count = 0; _total = _maximum = 0; Array.Clear(_buckets);
                return result;
            }
        }
    }

    private static readonly double[] Bounds = [1, 4, 8, 16, 33, 100, 250];
    private static readonly Distribution[] Metrics = Enum.GetValues<PerformanceMetric>().Select(_ => new Distribution()).ToArray();
    private sealed record SlowEvent(string Metric, double Milliseconds, int PaneId);
    private static readonly object SlowGate = new();
    private static readonly List<SlowEvent> SlowEvents = new(64);
    private static int _droppedSlowEvents;
    private static readonly string DirectoryPath = Environment.GetEnvironmentVariable("AGENTTERM_PERF_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "Diagnostics");
    private static readonly string Prefix = $"perf-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}";
    private static Timer? _timer;
    private static DispatcherQueue? _dispatcher;
    private static int _busy, _pending, _part;
    private static long _lastWrite;
    private static bool _failed;
    internal static bool Enabled => _timer is not null && !_failed;

    internal static void Start(DispatcherQueue dispatcher)
    {
        if (_timer is not null || Environment.GetEnvironmentVariable("AGENTTERM_PERF_CAPTURE") == "0") return;
        _dispatcher = dispatcher;
        _lastWrite = Stopwatch.GetTimestamp();
        _timer = new Timer(Tick, null, 100, 100);
    }

    internal static void Stop()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
    }

    internal static void Record(PerformanceMetric metric, double milliseconds, int paneId = 0)
    {
        if (!Enabled) return;
        Metrics[(int)metric].Add(milliseconds);
        if (paneId == 0 || milliseconds < 16) return;
        lock (SlowGate)
        {
            if (SlowEvents.Count < 64) SlowEvents.Add(new(metric.ToString(), milliseconds, paneId));
            else _droppedSlowEvents++;
        }
    }

    private static void Tick(object? state)
    {
        if (!Enabled || Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
            {
                var queued = Stopwatch.GetTimestamp();
                if (_dispatcher?.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    Record(PerformanceMetric.UiQueueDelay, Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                    Volatile.Write(ref _pending, 0);
                }) != true) Volatile.Write(ref _pending, 0);
            }
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastWrite, now).TotalSeconds < 5) return;
            var interval = Stopwatch.GetElapsedTime(_lastWrite, now).TotalSeconds;
            _lastWrite = now;
            SlowEvent[] slowEvents;
            int droppedSlowEvents;
            lock (SlowGate)
            {
                slowEvents = SlowEvents.ToArray();
                droppedSlowEvents = _droppedSlowEvents;
                SlowEvents.Clear();
                _droppedSlowEvents = 0;
            }
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, $"{Prefix}-{_part % 2}.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
            {
                path = Path.Combine(DirectoryPath, $"{Prefix}-{++_part % 2}.jsonl");
                File.WriteAllText(path, "");
            }
            using var process = Process.GetCurrentProcess();
            var record = new
            {
                schema = 2, utc = DateTime.UtcNow, processId = Environment.ProcessId,
                version = typeof(PerformanceCapture).Assembly.GetName().Version?.ToString(),
                moduleId = typeof(PerformanceCapture).Module.ModuleVersionId,
                cpuTotalMs = process.TotalProcessorTime.TotalMilliseconds,
                workingSetBytes = process.WorkingSet64,
                intervalSeconds = interval, bucketUpperBoundsMs = Bounds,
                slowEvents, droppedSlowEvents,
                metrics = Enum.GetValues<PerformanceMetric>().ToDictionary(metric => metric.ToString(), metric => Metrics[(int)metric].Take()),
                uiProbePending = Volatile.Read(ref _pending) != 0,
                allocatedBytes = GC.GetTotalAllocatedBytes(), managedHeapBytes = GC.GetTotalMemory(false),
                gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                gcPauseTotalMs = GC.GetTotalPauseDuration().TotalMilliseconds
            };
            File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
            foreach (var old in new DirectoryInfo(DirectoryPath).EnumerateFiles("perf-*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).Skip(8))
            {
                if (old.Name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _failed = true;
            Debug.WriteLine("AgentTerm performance capture disabled after a local storage failure.");
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
