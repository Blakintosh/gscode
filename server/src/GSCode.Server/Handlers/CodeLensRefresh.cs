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
        ClientRefresh.Request(_server, "workspace/codeLens/refresh");
    }
}

/// <summary>
/// Asks the client to re-request one family of editor decorations — <c>workspace/codeLens/refresh</c>,
/// <c>workspace/inlayHint/refresh</c>. One send for every family, so they cannot come to differ in
/// how a failure is handled.
///
/// A REQUEST per the spec, not a notification: the client answers with null. Fire-and-forget with
/// the fault observed, because a failed refresh is cosmetic — a client that does not support one
/// just errors, and nothing is lost beyond the delay the refresh exists to remove — and no caller is
/// waiting on it.
/// </summary>
internal static class ClientRefresh
{
    public static void Request(ILanguageServerFacade server, string method)
    {
        _ = server.SendRequest(method)
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
