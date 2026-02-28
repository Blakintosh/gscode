using Serilog;
using System.Diagnostics;

namespace GSCode.NET;

/// <summary>
/// Utility for tracking and logging performance metrics of operations.
/// Enable with FLAG_PERFORMANCE_TRACKING preprocessor directive.
/// </summary>
public class PerformanceTracker : IDisposable
{
    private readonly string _operationName;
    private readonly Stopwatch _stopwatch;
    private readonly Dictionary<string, object>? _metadata;
    private bool _disposed;

#if FLAG_PERFORMANCE_TRACKING
    public PerformanceTracker(string operationName, Dictionary<string, object>? metadata = null)
    {
        _operationName = operationName;
        _metadata = metadata;
        _stopwatch = Stopwatch.StartNew();
        
        if (_metadata != null && _metadata.Count > 0)
        {
            Log.Debug("[PERF START] {Operation} - {Metadata}", _operationName, string.Join(", ", _metadata.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        else
        {
            Log.Debug("[PERF START] {Operation}", _operationName);
        }
    }

    public void AddMetadata(string key, object value)
    {
        if (_metadata is null)
        {
            return;
        }
        _metadata[key] = value;
    }

    public void Checkpoint(string checkpointName)
    {
        // Include File metadata in checkpoint if available for better async tracking
        if (_metadata != null && _metadata.TryGetValue("File", out var fileName))
        {
            Log.Debug("[PERF CHECKPOINT] {Operation} - {Checkpoint}: {ElapsedMs} ms - File={File}", 
                _operationName, checkpointName, _stopwatch.ElapsedMilliseconds, fileName);
        }
        else
        {
            Log.Debug("[PERF CHECKPOINT] {Operation} - {Checkpoint}: {ElapsedMs} ms", 
                _operationName, checkpointName, _stopwatch.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopwatch.Stop();
        
        if (_metadata != null && _metadata.Count > 0)
        {
            Log.Debug("[PERF END] {Operation} completed in {ElapsedMs} ms - {Metadata}", 
                _operationName, 
                _stopwatch.ElapsedMilliseconds,
                string.Join(", ", _metadata.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        else
        {
            Log.Debug("[PERF END] {Operation} completed in {ElapsedMs} ms", _operationName, _stopwatch.ElapsedMilliseconds);
        }
    }
#else
    // No-op implementation when performance tracking is disabled
    public PerformanceTracker(string operationName, Dictionary<string, object>? metadata = null)
    {
        _operationName = operationName;
        _metadata = metadata;
        _stopwatch = Stopwatch.StartNew();
    }

    public void AddMetadata(string key, object value) { }
    public void Checkpoint(string checkpointName) { }
    public void Dispose() { }
#endif
}

/// <summary>
/// Extensions for easier performance tracking.
/// </summary>
public static class PerformanceTrackerExtensions
{
    /// <summary>
    /// Track an async operation's performance.
    /// </summary>
    public static async Task<T> TrackPerformanceAsync<T>(
        this Task<T> task, 
        string operationName, 
        Dictionary<string, object>? metadata = null)
    {
#if FLAG_PERFORMANCE_TRACKING
        using var tracker = new PerformanceTracker(operationName, metadata);
        var result = await task;
        return result;
#else
        return await task;
#endif
    }

    /// <summary>
    /// Track an async operation's performance (no return value).
    /// </summary>
    public static async Task TrackPerformanceAsync(
        this Task task, 
        string operationName, 
        Dictionary<string, object>? metadata = null)
    {
#if FLAG_PERFORMANCE_TRACKING
        using var tracker = new PerformanceTracker(operationName, metadata);
        await task;
#else
        await task;
#endif
    }
}
