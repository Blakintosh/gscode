using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.Server.Handlers;

/// <summary>
/// The one thing the dependent refresher asks of the connection: tell the client its code lenses
/// are out of date.
///
/// A seam for the same reason <see cref="IDiagnosticsSink"/> is one — <c>ILanguageServerFacade</c>
/// is a large interface, and a test that only needs to know whether a refresh was asked for should
/// not have to implement it.
/// </summary>
internal interface ICodeLensRefreshSink
{
    void Request();
}

/// <summary>The real sink: the LSP connection.</summary>
internal sealed class LanguageServerCodeLensRefreshSink : ICodeLensRefreshSink
{
    private readonly ILanguageServerFacade _server;

    public LanguageServerCodeLensRefreshSink(ILanguageServerFacade server)
    {
        _server = server;
    }

    public void Request()
    {
        // A REQUEST per the spec, not a notification: the client answers with null. Fire-and-forget
        // with the fault observed, because a failed refresh is cosmetic and this runs on a
        // background pass rather than on anything the user is waiting for.
        _ = _server.SendRequest("workspace/codeLens/refresh")
            .ReturningVoid(CancellationToken.None)
            .ContinueWith(static _ => { }, TaskScheduler.Default);
    }
}

/// <summary>A sink that does nothing, for tests and for a server with no connection to ask.</summary>
internal sealed class NullCodeLensRefreshSink : ICodeLensRefreshSink
{
    public static readonly NullCodeLensRefreshSink Instance = new();

    public void Request()
    {
    }
}
