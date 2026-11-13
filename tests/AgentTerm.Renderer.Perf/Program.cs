using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

internal static partial class Program
{
    private const string HeldMarker = "Held synchronized update";
    private static int _result;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: AgentTerm.Renderer.Perf <python-exe> <workload.py> <fresh-output.json> [options]");
            return 2;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var app = new RendererPerf.App();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
        });
        return _result;
    }

    internal static async void RunAsync(Application app)
    {
        var args = Environment.GetCommandLineArgs();
        var output = Path.GetFullPath(args[3]);
        var work = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-workload");
        var window = new Window { Title = "AgentTerm renderer performance fixture" };
        TerminalSession? session = null;
        TerminalSurface? surface = null;
        DispatcherQueueTimer? timer = null;
        DispatcherQueueTimer? heartbeat = null;
        EventHandler<object>? rendering = null;
        Process? presentMon = null;
        Task<string>? presentOutput = null, presentError = null;
        CompositorCapture? capture = null;
        var refreshEnabled = true;
        try
        {
            var hz = ReadInt("--hz", 60);
            var seconds = ReadInt("--seconds", 12);
            var refreshMs = ReadInt("--refresh-ms", 33);
            var idleSeconds = ReadInt("--idle-seconds", 2);
            var schedule = ReadOption("--schedule", "demand");
            var pattern = ReadOption("--pattern", "agent");
            if (schedule is not ("demand" or "deferred" or "timer" or "composition") || pattern is not ("agent" or "dense" or "cjk" or "held"))
                throw new ArgumentException("schedule must be demand/deferred/timer/composition; pattern must be agent/dense/cjk/held.");
            var outputDriven = schedule is "demand" or "deferred";
            if (File.Exists(output) || (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any()))
                throw new IOException("Use a fresh output path and workload directory.");
            Directory.CreateDirectory(work);
            surface = new TerminalSurface();
            surface.SetBottomAligned(true);
            surface.SetTerminalFont(14, "FiraCode Nerd Font");
            surface.SetAcrylicBackground(true);
            var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { surface } };
            root.Children.Add(new ProgressRing { Width = 28, Height = 28, IsActive = true, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
            window.Content = root;
            window.AppWindow.Resize(new SizeInt32(1040, 760));
            window.Activate();
            if (!args.Contains("--windowed")) (window.AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            await Task.Delay(1000);
            if (!surface.IsLoaded || surface.ActualWidth < 100 || surface.ActualHeight < 100)
                throw new Exception("Terminal surface never loaded with usable geometry.");
            if (args.Contains("--verify-transitions"))
            {
                await VerifyTransitionsAsync(window, surface, output);
                return;
            }
            var columns = (ushort)Math.Clamp(Math.Floor(surface.ActualWidth / surface.CellWidth), 2, 1000);
            var rows = (ushort)Math.Clamp(Math.Floor(surface.ActualHeight / surface.CellHeight), 1, 1000);
            session = new TerminalSession(columns, rows, new ShellProfile("perf-fixture", "Python workload", args[1],
                $"-u \"{Path.GetFullPath(args[2])}\" \"{work}\" --hz {hz} --seconds {seconds} --pattern {pattern}", Environment.CurrentDirectory));
            session.SetBottomAnchored(true);
            var outputEvent = typeof(TerminalSession).GetEvent("OutputAvailable");
            var pendingDelay = typeof(TerminalSession).GetProperty("PendingFrameRefreshDelay");
            if (outputDriven && (outputEvent is null || pendingDelay is null))
                throw new ArgumentException("This TerminalSession revision lacks OutputAvailable or PendingFrameRefreshDelay. Use --schedule timer.");
            var canvas = (CanvasControl)typeof(TerminalSurface).GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
            var glyphCache = GlyphCacheProbe.For(surface);
            var drawIntervals = new List<double>();
            var refreshDurations = new List<double>();
            var drawWorkDurations = new List<double>();
            var frameWorkDurations = new List<double>();
            var heartbeatIntervals = new List<double>();
            var outputToDraw = new List<double>();
            long outputArrival = 0, uncapturedArrival = 0, undrawnArrival = 0;
            var measuring = false;
            long lastDraw = 0, lastHeartbeat = 0;
            var draws = 0;
            var changedFrames = 0;
            long readFrames = 0, totalDraws = 0, renderingSubscriptions = 0, outputNotifications = 0, holdTimerFires = 0;
            var maxGlyphLayouts = 0;
            var pendingRefreshMs = 0d;
            TerminalFrame? lastFrame = null, expectedFinalFrame = null;
            var finalDraw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldShown = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            long goTimestamp = 0;
            Exception? callbackError = null;
            var completedEvent = typeof(TerminalSurface).GetEvent("DrawCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
            Action<double> completed = duration =>
            {
                if (!measuring) return;
                drawWorkDurations.Add(duration);
                frameWorkDurations.Add(pendingRefreshMs + duration);
                pendingRefreshMs = 0;
            };
            completedEvent?.GetAddMethod(true)?.Invoke(surface, [completed]);
            canvas.Draw += (_, _) =>
            {
                totalDraws++;
                if (glyphCache is not null) maxGlyphLayouts = Math.Max(maxGlyphLayouts, glyphCache.Count);
                if (expectedFinalFrame is not null && ReferenceEquals(expectedFinalFrame, lastFrame)) finalDraw.TrySetResult();
                if (pattern == "held" && goTimestamp != 0 && lastFrame is { } shown && FrameText(shown).Contains(HeldMarker))
                    heldShown.TrySetResult(Stopwatch.GetElapsedTime(goTimestamp).TotalMilliseconds);
                if (!measuring) { undrawnArrival = 0; return; }
                var now = Stopwatch.GetTimestamp();
                if (undrawnArrival != 0) outputToDraw.Add(Stopwatch.GetElapsedTime(undrawnArrival, now).TotalMilliseconds);
                undrawnArrival = 0;
                if (lastDraw != 0) drawIntervals.Add(Stopwatch.GetElapsedTime(lastDraw, now).TotalMilliseconds);
                lastDraw = now;
                draws++;
            };
            var holdTimer = window.DispatcherQueue.CreateTimer();
            holdTimer.IsRepeating = false;
            var outputQueued = 0;
            var renderingSubscribed = false;
            var frameRefreshQueued = false;
            var refreshPending = false;
            void Refresh()
            {
                try
                {
                    var start = Stopwatch.GetTimestamp();
                    refreshPending = false;
                    var arrival = Interlocked.Exchange(ref outputArrival, 0);
                    if (arrival != 0) uncapturedArrival = uncapturedArrival == 0 ? arrival : Math.Min(uncapturedArrival, arrival);
                    var frame = session.ReadFrame();
                    readFrames++;
                    surface.SetFrame(frame);
                    if (uncapturedArrival != 0 && lastFrame is { } shown && !ReferenceEquals(shown.Cells, frame.Cells))
                    {
                        undrawnArrival = undrawnArrival == 0 ? uncapturedArrival : Math.Min(undrawnArrival, uncapturedArrival);
                        uncapturedArrival = 0;
                    }
                    if (schedule == "demand" && !renderingSubscribed)
                    {
                        CompositionTarget.Rendering += rendering;
                        renderingSubscribed = true;
                        renderingSubscriptions++;
                    }
                    if (outputDriven)
                    {
                        holdTimer.Stop();
                        if (pendingDelay!.GetValue(session) is TimeSpan delay)
                        {
                            holdTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
                            holdTimer.Start();
                        }
                    }
                    if (measuring)
                    {
                        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        refreshDurations.Add(duration);
                        pendingRefreshMs += duration;
                        if (!ReferenceEquals(lastFrame, frame)) changedFrames++;
                    }
                    lastFrame = frame;
                }
                catch (Exception error) { callbackError = error; }
            }
            if (outputDriven)
                rendering = (_, _) =>
                {
                    CompositionTarget.Rendering -= rendering;
                    renderingSubscribed = false;
                    if (frameRefreshQueued || !refreshEnabled || (schedule == "demand" && !refreshPending)) return;
                    refreshPending = false;
                    frameRefreshQueued = window.DispatcherQueue.TryEnqueue(() =>
                    {
                        frameRefreshQueued = false;
                        if (refreshEnabled) Refresh();
                    });
                };
            void RequestFrameRefresh()
            {
                if (!refreshEnabled || frameRefreshQueued) return;
                if (schedule == "demand")
                {
                    if (renderingSubscribed) refreshPending = true;
                    else Refresh();
                    return;
                }
                if (renderingSubscribed) return;
                CompositionTarget.Rendering += rendering;
                renderingSubscribed = true;
                renderingSubscriptions++;
            }
            Action outputAvailable = () =>
            {
                Interlocked.Increment(ref outputNotifications);
                Interlocked.CompareExchange(ref outputArrival, Stopwatch.GetTimestamp(), 0);
                if (!refreshEnabled || Interlocked.Exchange(ref outputQueued, 1) != 0) return;
                if (!window.DispatcherQueue.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref outputQueued, 0);
                    RequestFrameRefresh();
                })) Interlocked.Exchange(ref outputQueued, 0);
            };
            if (outputDriven)
            {
                outputEvent!.AddEventHandler(session, outputAvailable);
                holdTimer.Tick += (_, _) => { holdTimerFires++; RequestFrameRefresh(); };
            }
            await Task.Run(session.Start);
            if (outputDriven) RequestFrameRefresh();
            else if (schedule == "composition")
            {
                var compositionQueued = false;
                rendering = (_, _) =>
                {
                    if (compositionQueued) return;
                    compositionQueued = true;
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        compositionQueued = false;
                        if (refreshEnabled) Refresh();
                    });
                };
                CompositionTarget.Rendering += rendering;
                renderingSubscriptions++;
            }
            else
            {
                timer = window.DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(refreshMs);
                timer.Tick += (_, _) => Refresh();
                timer.Start();
            }
            heartbeat = window.DispatcherQueue.CreateTimer();
            heartbeat.Interval = TimeSpan.FromMilliseconds(16);
            heartbeat.Tick += (_, _) =>
            {
                if (!measuring) return;
                var now = Stopwatch.GetTimestamp();
                if (lastHeartbeat != 0) heartbeatIntervals.Add(Stopwatch.GetElapsedTime(lastHeartbeat, now).TotalMilliseconds);
                lastHeartbeat = now;
            };
            heartbeat.Start();
            var readiness = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(work, "ready.json")) && readiness.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            if (!File.Exists(Path.Combine(work, "ready.json"))) throw new Exception("Workload never signaled readiness.");
            await Task.Delay(500);
            if (args.Contains("--capture-frames")) capture = new CompositorCapture(window, surface);
            var presentMonPath = ReadOption("--presentmon", "");
            var presentStatus = "not-requested";
            var presentCsv = Path.ChangeExtension(output, ".present.csv");
            if (presentMonPath.Length > 0)
            {
                var info = new ProcessStartInfo(presentMonPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "--process_id", Environment.ProcessId.ToString(), "--session_name", $"AgentTermPerf-{Environment.ProcessId}",
                    "--timed", (seconds + 2).ToString(), "--terminate_after_timed", "--qpc_time_ms", "--output_file", presentCsv, "--no_console_stats" }) info.ArgumentList.Add(value);
                presentMon = Process.Start(info)!;
                presentOutput = presentMon.StandardOutput.ReadToEndAsync();
                presentError = presentMon.StandardError.ReadToEndAsync();
                await Task.Delay(200);
                presentStatus = presentMon.HasExited ? $"collector-exited-{presentMon.ExitCode}" : "recording";
            }
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBytesStart = process.PrivateMemorySize64;
            var glyphLayoutsCreatedStart = glyphCache?.Created ?? 0;
            var cpuStart = process.TotalProcessorTime;
            var allocatedStart = GC.GetTotalAllocatedBytes();
            var pausesStart = GC.GetTotalPauseDuration();
            var generations = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var measurementStartQpc = Stopwatch.GetTimestamp();
            var started = Stopwatch.StartNew();
            capture?.Begin();
            uncapturedArrival = undrawnArrival = 0;
            Interlocked.Exchange(ref outputArrival, 0);
            measuring = true;
            goTimestamp = Stopwatch.GetTimestamp();
            File.WriteAllText(Path.Combine(work, "run-1.go"), "go");
            var done = Path.Combine(work, "run-1.done.json");
            while (!File.Exists(done) && started.Elapsed < TimeSpan.FromSeconds(seconds + 15)) await Task.Delay(50);
            double? heldLatencyMs = pattern == "held" ? await heldShown.Task.WaitAsync(TimeSpan.FromSeconds(5)) : null;
            measuring = false;
            var compositorCapture = capture?.End(output);
            var measurementEndQpc = Stopwatch.GetTimestamp();
            var elapsed = started.Elapsed.TotalSeconds;
            var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            var allocated = GC.GetTotalAllocatedBytes() - allocatedStart;
            var pauseMs = (GC.GetTotalPauseDuration() - pausesStart).TotalMilliseconds;
            var gcCounts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - generations[i]).ToArray();
            var glyphLayoutsCreated = (glyphCache?.Created ?? 0) - glyphLayoutsCreatedStart;
            process.Refresh();
            var privateBytesEnd = process.PrivateMemorySize64;

            if (!File.Exists(done)) throw new Exception("Workload failed to produce its completion marker.");
            var workload = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(done));
            if (workload.GetProperty("idleUntil").GetString() != "run-2.go" || File.Exists(Path.Combine(work, "run-2.go")))
                throw new Exception("Workload did not acknowledge silence until the next trial's start signal.");
            await Task.Delay(300);
            var idleReads = readFrames;
            var idleDraws = totalDraws;
            var idleSubscriptions = renderingSubscriptions;
            var idleCpu = process.TotalProcessorTime;
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
            process.Refresh();
            var idle = new
            {
                seconds = idleSeconds, readFrames = readFrames - idleReads, draws = totalDraws - idleDraws,
                renderingSubscriptions = renderingSubscriptions - idleSubscriptions,
                cpuSeconds = (process.TotalProcessorTime - idleCpu).TotalSeconds
            };

            timer?.Stop();
            holdTimer.Stop();
            refreshEnabled = false;
            if (rendering is not null) { CompositionTarget.Rendering -= rendering; rendering = null; }
            if (outputDriven) outputEvent!.RemoveEventHandler(session, outputAvailable);
            heartbeat.Stop();
            if (callbackError is not null) throw new Exception("Refresh callback failed.", callbackError);
            var expectedTicks = pattern == "held" ? 1 : hz * seconds;
            if (workload.GetProperty("ticks").GetInt32() != expectedTicks || workload.GetProperty("elapsed").GetDouble() > seconds * 1.1)
                throw new Exception("Workload did not complete its configured rate within ten percent of its duration.");
            if (heldLatencyMs is < 900 or > 2500)
                throw new Exception($"Held synchronized update appeared after {heldLatencyMs:0} ms; expected the 1000 ms hold timeout.");
            var finalFrame = session.ReadFrame();
            var text = FrameText(finalFrame);
            var marker = pattern == "held" ? HeldMarker : "Agent task";
            var minimumUpdates = Math.Min(10, expectedTicks);
            if (draws < minimumUpdates || changedFrames < minimumUpdates || !text.Contains(marker) || TerminalGridGeometry.VisibleRows(finalFrame) == 0)
                throw new Exception($"Work did not render. draws={draws}, changedFrames={changedFrames}, workloadText={text.Contains(marker)}.");
            if (glyphCache is not null && maxGlyphLayouts > glyphCache.Capacity)
                throw new Exception($"Glyph cache held {maxGlyphLayouts} layouts above its {glyphCache.Capacity} capacity.");
            lastFrame = expectedFinalFrame = finalFrame;
            surface.SetFrame(finalFrame);
            canvas.Invalidate();
            try { await finalDraw.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                var ticks = 0;
                EventHandler<object> probe = (_, _) => ticks++;
                CompositionTarget.Rendering += probe;
                var drawsBefore = totalDraws;
                canvas.Invalidate();
                await Task.Delay(1000);
                CompositionTarget.Rendering -= probe;
                var current = typeof(TerminalSurface).GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface);
                throw new TimeoutException($"Final draw missing. ticks={ticks} drawsAfterProbe={totalDraws - drawsBefore} sameCanvas={ReferenceEquals(current, canvas)} canvasLoaded={canvas.IsLoaded} ready={canvas.ReadyToDraw} visible={window.Visible} surfaceLoaded={surface.IsLoaded} size={canvas.ActualWidth}x{canvas.ActualHeight}");
            }
            var gridTop = (float)TerminalGridGeometry.For(finalFrame, surface.ActualHeight, surface.CellWidth, surface.CellHeight, true).Top;
            using var target = new CanvasRenderTarget(canvas, (float)surface.ActualWidth, (float)surface.ActualHeight, canvas.Dpi);
            using (var drawing = target.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, finalFrame, surface.CellWidth, surface.CellHeight, true, true,
                    transparentBackground: true, fontFamily: surface.TerminalFontFamily, hideCursor: true, gridTop: gridTop);
            var pixels = target.GetPixelBytes();
            if (!pixels.Any(value => value != 0)) throw new Exception("Final rendered frame is blank.");
            var retained = typeof(TerminalSurface).GetField("_renderTarget", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(surface) as CanvasRenderTarget;
            var retainedStatus = retained is null ? "not-available" : "exact-reference-match";
            if (retained is not null && !retained.GetPixelBytes().SequenceEqual(pixels))
                throw new Exception("Onscreen retained raster differs from the full reference renderer.");
            await (retained ?? target).SaveAsync(Path.ChangeExtension(output, ".png"), CanvasBitmapFileFormat.Png);
            if (presentMon is not null)
            {
                await presentMon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                File.WriteAllText(Path.ChangeExtension(output, ".present.log"), (await presentOutput!) + (await presentError!));
                presentStatus = presentMon.ExitCode == 0 && File.Exists(presentCsv) ? "capture-completed-review-csv-coverage" : $"collector-exited-{presentMon.ExitCode}";
            }
            var result = new
            {
                elapsedSeconds = elapsed, cpuSeconds, allocatedBytes = allocated, gcPauseMs = pauseMs, gcCounts,
                draws, changedFrames, columns, rows, surfaceWidth = surface.ActualWidth, surfaceHeight = surface.ActualHeight,
                cellWidth = surface.CellWidth, cellHeight = surface.CellHeight, font = surface.TerminalFontFamily, dpi = canvas.Dpi,
                rasterizationScale = surface.XamlRoot.RasterizationScale,
                fixtureSchemaVersion = 4,
                workloadHz = hz, workloadPattern = pattern,
                refreshSchedule = schedule switch { "demand" => "output-demand", "deferred" => "output-demand-deferred", "composition" => "composition-continuous", _ => "timer" },
                refreshIntervalMs = schedule == "timer" ? refreshMs : (int?)null,
                readFrames, renderingSubscriptions, outputNotifications, holdTimerFires, heldLatencyMs, idle,
                drawIntervals = Stats(drawIntervals), outputToDrawLatency = outputDriven ? Stats(outputToDraw) : null, refreshDurations = Stats(refreshDurations), heartbeatIntervals = Stats(heartbeatIntervals),
                drawWorkAvailability = completedEvent is null ? "not-available" : "callback-elapsed-only",
                drawWorkDurations = completedEvent is null ? null : Stats(drawWorkDurations),
                frameWorkDurations = completedEvent is null ? null : Stats(frameWorkDurations), retainedRaster = retainedStatus,
                glyphCache = glyphCache is null ? null : new { capacity = glyphCache.Capacity, maxLayouts = maxGlyphLayouts, created = glyphLayoutsCreated },
                privateBytesStart, privateBytesEnd,
                measurementStartQpcMs = measurementStartQpc * 1000d / Stopwatch.Frequency,
                measurementEndQpcMs = measurementEndQpc * 1000d / Stopwatch.Frequency,
                presentedFrameCapture = presentStatus, presentedFrameCsv = File.Exists(presentCsv) ? presentCsv : null,
                compositorCapture,
                frameHash = Convert.ToHexString(SHA256.HashData(pixels)), nonblankCells = finalFrame.Cells.Count(cell => !string.IsNullOrWhiteSpace(cell.Text)), workload
            };
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(File.ReadAllText(output));
        }
        catch (Exception error)
        {
            _result = 1;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output + ".error.txt", error.ToString());
            Console.Error.WriteLine(error);
        }
        finally
        {
            timer?.Stop();
            refreshEnabled = false;
            heartbeat?.Stop();
            if (rendering is not null) CompositionTarget.Rendering -= rendering;
            if (presentMon is not null)
            {
                if (!presentMon.HasExited) presentMon.Kill();
                presentMon.Dispose();
            }
            capture?.Dispose();
            if (session is not null) await session.DisposeAsync();
            surface?.ReleaseResources();
            window.Close();
            app.Exit();
        }

        string ReadOption(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 == args.Length) throw new ArgumentException($"Missing value for {name}.");
            return args[index + 1];
        }
        int ReadInt(string name, int fallback)
        {
            if (!int.TryParse(ReadOption(name, fallback.ToString(CultureInfo.InvariantCulture)), out var value) || value <= 0)
                throw new ArgumentException($"{name} must be a positive integer.");
            return value;
        }
    }

    private static string FrameText(TerminalFrame frame) => string.Concat(frame.Cells.Select(cell => cell.Text));

    private static object Stats(List<double> values)
    {
        values.Sort();
        return new { count = values.Count, medianMs = At(0.5), p95Ms = At(0.95), maxMs = At(1), over16_6Ms = values.Count(value => value > 16.6) };
        double At(double percentile) => values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)(percentile * values.Count))];
    }
}

internal sealed class GlyphCacheProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly object _cache;
    private readonly FieldInfo _layouts, _capacity, _created;

    private GlyphCacheProbe(object cache, FieldInfo layouts, FieldInfo capacity, FieldInfo created) =>
        (_cache, _layouts, _capacity, _created) = (cache, layouts, capacity, created);

    internal int Count => ((System.Collections.ICollection)_layouts.GetValue(_cache)!).Count;
    internal int Capacity => (int)_capacity.GetValue(_cache)!;
    internal long Created => (long)_created.GetValue(_cache)!;

    internal static GlyphCacheProbe? For(TerminalSurface surface)
    {
        if (typeof(TerminalSurface).GetField("_glyphLayouts", Private)?.GetValue(surface) is not { } cache) return null;
        var type = cache.GetType();
        return type.GetField("_layouts", Private) is { } layouts && type.GetField("_capacity", Private) is { } capacity &&
            type.GetField("_created", Private) is { } created ? new(cache, layouts, capacity, created) : null;
    }
}
