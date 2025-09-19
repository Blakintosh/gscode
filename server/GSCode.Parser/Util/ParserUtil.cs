using GSCode.Data.Models;
using GSCode.Parser.Lexical;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GSCode.Parser.Util;

internal static partial class ParserUtil
{
    /// <summary>
    /// Converts a series of tokens into a script path string, if it matches.
    /// </summary>
    /// <param name="baseToken">First token in the path</param>
    /// <param name="useExtension">Whether to check for an extension</param>
    /// <param name="lastToken">Output final token in the path</param>
    /// <returns>A string containing a file path if successful, otherwise null</returns>
    public static string? ConvertImportSequenceToString(Token baseToken, bool useExtension, out Token lastToken)
    {
        //lastToken = baseToken;

        //if (!baseToken.Is(TokenType.Name) || baseToken.IsEof())
        //{
        //    return null;
        //}

        //StringBuilder builder = new();
        //builder.Append(baseToken.Contents);

        //Token token = baseToken.NextAny();

        //while(token.Is(TokenType.SpecialToken, SpecialTokenTypes.Backslash))
        //{
        //    builder.Append('\\');

        //    Token nextToken = token.NextAny();
        //    if(!nextToken.Is(TokenType.Name) || nextToken.IsEof())
        //    {
        //        return null;
        //    }
        //    builder.Append(nextToken.Contents);

        //    token = nextToken.NextAny();

        //    // . extension
        //    if(useExtension && !token.IsEof() && token.Is(TokenType.Operator, OperatorTypes.MemberAccess))
        //    {
        //        Token finalToken = token.NextAny();
        //        builder.Append(token.Contents);

        //        if(finalToken.Is(TokenType.Name))
        //        {
        //            builder.Append(finalToken.Contents);
        //            lastToken = finalToken;
        //            return builder.ToString();
        //        }
        //        return null;
        //    }
        //}

        //lastToken = token;
        //return builder.ToString();
        throw new NotImplementedException();
    }

    /// <summary>
    /// Attempts to find the script file relative to the current path, or otherwise from TA_TOOLS_PATH.
    /// </summary>
    /// <param name="currentScriptPath">The path of the current script file.</param>
    /// <param name="desiredScriptPath">The script path to locate.</param>
    /// <returns>A file path string if found, or null if the file doesn't exist.</returns>
    public static string? GetScriptFilePath(string currentScriptPath, string desiredScriptPath)
    {
        const string baseDir = "/scripts/";
        string scriptPath = NormalisePath(desiredScriptPath);
        string? basePath = ExtractBasePath(currentScriptPath, baseDir);

        // Check within the base path
        if (!string.IsNullOrEmpty(basePath) && ScriptFileExists(basePath, scriptPath))
        {
            return Path.Combine(basePath, scriptPath);
        }

        // Check within the TA_TOOLS_PATH environment variable path
        string? toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
        if (!string.IsNullOrEmpty(toolsPath))
        {
            string sharedPath = Path.Combine(toolsPath, "share", "raw");
            if (ScriptFileExists(sharedPath, scriptPath))
            {
                return Path.Combine(sharedPath, scriptPath);
            }
        }

        // Return null if the script file is not found
        return null;
    }

    /// <summary>
    /// Normalises the desired script path by replacing backslashes with forward slashes.
    /// </summary>
    /// <param name="path">The original desired script path.</param>
    /// <returns>A normalised script path with forward slashes.</returns>
    private static string NormalisePath(string path)
    {
        return path.Replace("\\", "/");
    }

    /// <summary>
    /// Extracts the base path up to the specified base directory within the current script path.
    /// </summary>
    /// <param name="currentPath">The full path of the current script file.</param>
    /// <param name="baseDir">The base directory to locate.</param>
    /// <returns>The base path up to the base directory, or null if the directory is not found.</returns>
    private static string? ExtractBasePath(string currentPath, string baseDir)
    {
        string normalisedPath = currentPath.Replace("\\", "/");
        int baseIndex = normalisedPath.LastIndexOf(baseDir, StringComparison.OrdinalIgnoreCase);

        if (baseIndex >= 0)
        {
            string basePath = normalisedPath.Substring(0, baseIndex);

            // Remove leading slash on drive letter if applicable.
            if (OperatingSystem.IsWindows() && DrivePrefixRegex().IsMatch(basePath))
            {
                return basePath[1..];
            }
            return basePath;
        }

        return null;
    }

    /// <summary>
    /// Checks if the script file exists in the specified base path combined with the script path.
    /// </summary>
    /// <param name="basePath">The base directory to search within.</param>
    /// <param name="scriptPath">The relative script path.</param>
    /// <returns>True if the file exists, otherwise false.</returns>
    private static bool ScriptFileExists(string basePath, string scriptPath)
    {
        return File.Exists(Path.Combine(basePath, scriptPath));
    }


    public static string? GetCommentContents(string? commentContents, TokenType tokenType)
    {
        // TODO: this function is gross
        if (commentContents == null)
        {
            return null;
        }

        ReadOnlySpan<char> contentSpan = commentContents;

        int sliceIndex = GetIndexForSliceStart(contentSpan);

        if (sliceIndex >= contentSpan.Length)
        {
            return null;
        }

        contentSpan = tokenType == TokenType.LineComment ? contentSpan[sliceIndex..] : contentSpan.Slice(sliceIndex, contentSpan.Length - sliceIndex - 2);

        StringBuilder builder = new();

        BuildCleanedCommentContents(contentSpan, builder);

        return builder.ToString();
    }

    private static void BuildCleanedCommentContents(ReadOnlySpan<char> contentSpan, StringBuilder builder)
    {
        bool inWhitespace = false;
        for (int i = 0; i < contentSpan.Length; i++)
        {
            char current = contentSpan[i];
            if (char.IsWhiteSpace(current))
            {
                inWhitespace = true;
                continue;
            }
            // Reduce allocations by only appending the whitespace once exited, removing need for trailing trim
            else if (inWhitespace)
            {
                builder.Append(' ');
                inWhitespace = false;
            }

            builder.Append(current);
        }
    }

    private static int GetIndexForSliceStart(ReadOnlySpan<char> contentSpan)
    {
        for (int sliceIndex = 2; sliceIndex < contentSpan.Length; sliceIndex++)
        {
            if (!char.IsWhiteSpace(contentSpan[sliceIndex]))
            {
                return sliceIndex;
            }
        }
        return contentSpan.Length;
    }

    /// <summary>
    /// Produces a human-readable standard formatted code snippet corresponding to the list of tokens provided.
    /// </summary>
    /// <param name="tokensSource">List of tokens to convert to readable format</param>
    /// <returns>A string containing the readable code for these tokens</returns>
    public static string ProduceSnippetString(List<Token> tokensSource)
    {
        ReadOnlySpan<Token> tokenSpan = CollectionsMarshal.AsSpan(tokensSource);
        if (tokenSpan.Length == 0) return string.Empty;

        int startIndex = tokenSpan[0].Type == TokenType.Whitespace ? 1 : 0;
        int endIndex = tokenSpan[^1].Type == TokenType.Whitespace ? tokenSpan.Length - 1 : tokenSpan.Length;

        int capacity = 0;
        for (int i = startIndex; i < endIndex; i++)
        {
            capacity += tokensSource[i].Lexeme?.Length ?? 0;
        }

        var sb = new StringBuilder(Math.Max(0, capacity));
        for (int i = startIndex; i < endIndex; i++)
        {
            sb.Append(tokensSource[i].Lexeme);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^/[a-zA-Z]:")]
    private static partial Regex DrivePrefixRegex();
}