using GSCode.Server.Configuration;
using GSCode.Server.Startup;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GSCode.Server.Tests.Startup;

/// <summary>
/// After indexing, the server runs one blocking, compacting collection to hand the index's
/// transient memory back to the OS. Its pause grows with the live heap (about 120 ms at a stock
/// install, 4 s at 50,000 heavy files), so it runs only up to a limit. The limit is the user's:
/// <c>gscode.memory.compactionLimitMB</c>, where 0 never compacts.
/// </summary>
public class CompactionLimitTests
{
    private const long Megabyte = 1024 * 1024;

    private static JToken Payload(string body)
    {
        return JToken.Parse("{ \"gscode\": " + body + " }");
    }

    [Fact]
    public void TheDefaultLimit_IsOneGigabyte()
    {
        Assert.Equal(1024, new ServerSettings().MemoryCompactionLimitMegabytes);
    }

    [Fact]
    public void TheLimit_IsReadFromEitherPayloadShape_AndANegativeOneIsClampedToZero()
    {
        ServerSettings settings = new();

        settings.Apply(Payload("{ \"memory.compactionLimitMB\": 4096 }"));
        Assert.Equal(4096, settings.MemoryCompactionLimitMegabytes);

        settings.Apply(Payload("{ \"memory\": { \"compactionLimitMB\": 512 } }"));
        Assert.Equal(512, settings.MemoryCompactionLimitMegabytes);

        settings.Apply(Payload("{ \"memory.compactionLimitMB\": -5 }"));
        Assert.Equal(0, settings.MemoryCompactionLimitMegabytes);
    }

    [Theory]
    [InlineData(83, 1024, true)]
    [InlineData(1024, 1024, true)]
    [InlineData(1025, 1024, false)]
    [InlineData(9800, 16384, true)]
    [InlineData(1, 0, false)]
    public void TheHeap_IsCompactedOnlyUpToTheLimit(long liveMegabytes, int limitMegabytes, bool compacts)
    {
        Assert.Equal(compacts, StartupIndexRunner.ShouldCompact(liveMegabytes * Megabyte, limitMegabytes));
    }
}
