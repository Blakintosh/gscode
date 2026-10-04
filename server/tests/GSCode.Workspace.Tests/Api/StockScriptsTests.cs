using GSCode.Core;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

public class StockScriptsTests
{
    [Fact]
    public void Load_ALockedFile_ReturnsEmptyRatherThanThrowing()
    {
        // The doc comment already promises "missing/unreadable, rather than throwing" — but only
        // File.Exists (missing) was actually handled. File.ReadLines defers the real open to the
        // foreach's first iteration, so a file that EXISTS but is locked by another process threw
        // straight out of Load, uncaught.
        GameProfile bo3 = GameProfile.BlackOps3;
        string directory = Path.Combine(Path.GetTempPath(), $"gscode_stock_scripts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, bo3.StockScriptsFileName!);
        File.WriteAllText(path, "scripts/foo.gsc\n");

        try
        {
            using FileStream exclusiveLock = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            StockScripts loaded = StockScripts.Load(directory, bo3);

            Assert.Equal(0, loaded.Count);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
