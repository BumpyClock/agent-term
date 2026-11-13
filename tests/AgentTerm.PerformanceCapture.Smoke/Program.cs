using System.Text.Json;
using AgentTerm_App;
using Microsoft.UI.Dispatching;

var mode = args.Single();
var directory = Path.Combine(Path.GetTempPath(), "AgentTerm-Capture-Smoke-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("AGENTTERM_PERF_DIRECTORY", directory);
Environment.SetEnvironmentVariable("AGENTTERM_PERF_CAPTURE", mode == "disabled" ? "0" : "1");
if (mode == "storage-failure") File.WriteAllText(directory, "a file prevents directory creation");
try
{
    PerformanceCapture.Start(new DispatcherQueue());
    for (var index = 0; index < 100; index++) PerformanceCapture.Record(PerformanceMetric.Draw, 20, 7);
    if (mode == "disabled")
    {
        await Task.Delay(300);
        if (PerformanceCapture.Enabled || Directory.Exists(directory)) throw new Exception("Disabled capture wrote data.");
    }
    else
    {
        await Task.Delay(6200);
        if (mode == "storage-failure")
        {
            if (PerformanceCapture.Enabled) throw new Exception("Storage failure did not disable capture.");
        }
        else
        {
            var lines = File.ReadAllLines(Directory.GetFiles(directory, "perf-*.jsonl").Single());
            using var document = JsonDocument.Parse(lines.Single());
            var root = document.RootElement;
            var draw = root.GetProperty("metrics").GetProperty("Draw");
            if (draw.GetProperty("count").GetInt64() != 100 || draw.GetProperty("meanMs").GetDouble() != 20)
                throw new Exception("Aggregate counts or units incorrect.");
            if (root.GetProperty("slowEvents").GetArrayLength() != 64 || root.GetProperty("droppedSlowEvents").GetInt32() != 36)
                throw new Exception("Slow-event buffer not bounded.");
            if (root.GetProperty("slowEvents")[0].GetProperty("PaneId").GetInt32() != 7)
                throw new Exception("Pane attribution missing.");
            if (root.GetProperty("metrics").GetProperty("UiQueueDelay").GetProperty("count").GetInt64() == 0)
                throw new Exception("Dispatcher probe did not run.");
            if (lines[0].Contains(directory, StringComparison.Ordinal)) throw new Exception("Export leaked capture path.");
        }
    }
    Console.WriteLine($"PASS {mode}");
}
finally
{
    PerformanceCapture.Stop();
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
    else if (File.Exists(directory)) File.Delete(directory);
}

namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Normal }
    public sealed class DispatcherQueue
    {
        public bool TryEnqueue(DispatcherQueuePriority priority, Action callback)
        {
            callback();
            return true;
        }
    }
}
