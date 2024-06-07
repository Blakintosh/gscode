using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Services;

internal class WatchedScriptFile
{
    public string FilePath { get; }
    public FileSystemWatcher Watcher { get; }
    public List<string> Dependencies { get; } = new();
}

/// <summary>
/// Responsible for monitoring scripts that change and ensuring their funciton signatures are kept up to date
/// </summary>
internal class ScriptWatcherService
{
    ConcurrentDictionary<string, WatchedScriptFile> WatchedScripts { get; } = new();
}
