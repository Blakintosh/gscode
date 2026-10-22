using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A client that sends <c>textDocument</c> capabilities without one of them — <c>completion</c> left
/// out — has each handler's registration asked with a NULL capability. CompletionHandler read a
/// field off it, and the exception, thrown inside the initialize request, left that request
/// unanswered: the server sat silent, and the client waited forever. VS Code always sends
/// completion, so only another client, or a script driving the server over stdio, met it.
/// </summary>
public sealed class RegistrationWithoutCapabilityTests
{
    [Fact]
    public async Task CompletionRegistersWhenTheClientSendsNoCompletionCapability()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CompletionHandler handler = new(
            workspace.Navigation, workspace.Completion, new ServerSettings(), HandlerWorkspace.Selector, workspace.Builtins);

        CompletionRegistrationOptions options =
            ((IRegistration<CompletionRegistrationOptions, CompletionCapability>)handler).GetRegistrationOptions(null!, new ClientCapabilities());

        Assert.NotNull(options.TriggerCharacters);
    }
}
