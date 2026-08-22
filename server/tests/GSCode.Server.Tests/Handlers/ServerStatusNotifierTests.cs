using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The status-bar tooltip's memory figure was set once, from the gscode/indexingComplete payload,
/// and never again — so it showed whatever the server held the instant indexing finished, which
/// is both the least interesting moment to sample and stale a minute later.
///
/// These pin the payload's shape, which is the part that crosses into TypeScript. The loop itself
/// is a timer over Environment.WorkingSet and is not worth a fake clock.
/// </summary>
public class ServerStatusNotifierTests
{
    [Fact]
    public void TheStatusPayloadCarriesMegabytes()
    {
        // The client formats with toFixed(0), so megabytes — not bytes — is the contract.
        ServerStatusParams status = new(212.5);

        Assert.Equal(212.5, status.WorkingSetMegabytes);
    }

    [Fact]
    public void TheCompletePayloadStillCarriesAStartingValue()
    {
        // The tooltip has to be complete before the first status push arrives, so indexingComplete
        // seeds the number and gscode/serverStatus keeps it current from there.
        IndexingCompleteParams complete = new(
            FilesIndexed: 1105, TotalFiles: 1105, ElapsedMilliseconds: 4200, WorkingSetMegabytes: 212.0);

        Assert.Equal(212.0, complete.WorkingSetMegabytes);
        Assert.Equal(1105, complete.FilesIndexed);
    }
}
