using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// The commands and menus in <c>client/package.json</c> against the code that serves them.
///
/// Nothing compiles one against the other. A declared command nobody registers fails with
/// "command not found" when the palette or a context menu runs it; and a menu entry naming a
/// command that is not declared is shown with no title, or not at all.
/// </summary>
public class ClientCommandsTests
{
    private static DirectoryInfo FindClientDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while ( directory is not null )
        {
            if ( File.Exists(Path.Combine(directory.FullName, "client", "package.json")) )
            {
                return new DirectoryInfo(Path.Combine(directory.FullName, "client"));
            }

            directory = directory.Parent;
        }

        // Loud rather than vacuous: a quiet return would read as every command checked out.
        Assert.Fail($"Could not find client/package.json walking up from {AppContext.BaseDirectory}.");
        return null!;
    }

    private static JsonElement Contributes(DirectoryInfo client, out JsonDocument document)
    {
        document = JsonDocument.Parse(File.ReadAllText(Path.Combine(client.FullName, "package.json")));
        return document.RootElement.GetProperty("contributes");
    }

    private static string ClientSources(DirectoryInfo client)
    {
        return string.Concat(Directory
            .EnumerateFiles(Path.Combine(client.FullName, "src"), "*.ts")
            .Select(File.ReadAllText));
    }

    [Fact]
    public void EveryDeclaredCommandIsRegistered()
    {
        DirectoryInfo client = FindClientDirectory();
        JsonElement contributes = Contributes(client, out JsonDocument document);
        using JsonDocument _ = document;
        string sources = ClientSources(client);

        List<string> missing = [];
        foreach ( JsonElement command in contributes.GetProperty("commands").EnumerateArray() )
        {
            string id = command.GetProperty("command").GetString()!;
            if ( !Regex.IsMatch(sources, @"registerCommand\(\s*""" + Regex.Escape(id) + @"""") )
            {
                missing.Add(id);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryMenuEntryNamesADeclaredCommandOrSubmenu()
    {
        DirectoryInfo client = FindClientDirectory();
        JsonElement contributes = Contributes(client, out JsonDocument document);
        using JsonDocument _ = document;

        HashSet<string> commands = [.. contributes.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("command").GetString()!)];
        HashSet<string> submenus = contributes.TryGetProperty("submenus", out JsonElement declared)
            ? [.. declared.EnumerateArray().Select(submenu => submenu.GetProperty("id").GetString()!)]
            : [];

        List<string> unknown = [];
        foreach ( JsonProperty menu in contributes.GetProperty("menus").EnumerateObject() )
        {
            foreach ( JsonElement entry in menu.Value.EnumerateArray() )
            {
                if ( entry.TryGetProperty("command", out JsonElement command)
                    && !commands.Contains(command.GetString()!) )
                {
                    unknown.Add(menu.Name + ": " + command.GetString());
                }

                if ( entry.TryGetProperty("submenu", out JsonElement submenu)
                    && !submenus.Contains(submenu.GetString()!) )
                {
                    unknown.Add(menu.Name + ": submenu " + submenu.GetString());
                }
            }
        }

        Assert.Empty(unknown);
    }
}
