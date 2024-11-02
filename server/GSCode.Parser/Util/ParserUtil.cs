using GSCode.Data.Models;
using GSCode.Parser.Lexical;
using System.Runtime.InteropServices;
using System.Text;

namespace GSCode.Parser.Util;

internal static class ParserUtil
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
    /// <param name="currentScriptPath">Current script file's path</param>
    /// <param name="desiredScriptPath">Script path to determine location of</param>
    /// <param name="extension">Extension to append if applicable</param>
    /// <returns>A file path string if found, or null otherwise</returns>
    public static string? GetScriptFilePath(string currentScriptPath, string desiredScriptPath, string? extension = null)
    {
        string baseDir = @"/scripts/";

        string scriptPath = desiredScriptPath.Replace("\\", "/");
        if(!string.IsNullOrEmpty(extension))
        {
            scriptPath += $".{extension}";
        }

        string normalisedCurrentScriptPath = currentScriptPath.Replace("\\", "/");

        string? basePath = null;
        if (normalisedCurrentScriptPath.Contains(baseDir))
        {
            basePath = normalisedCurrentScriptPath[..normalisedCurrentScriptPath.LastIndexOf(baseDir)];
        }

        // TODO: this is stupid because what if it's /f: etc.
        // If basePath starts with /c:, remove the leading /
        if (basePath != null && basePath.StartsWith("/c:"))
        {
            basePath = basePath[1..];
        }

        if (!string.IsNullOrEmpty(basePath) && ScriptFileExists(basePath, scriptPath))
        {
            return Path.Combine(basePath, scriptPath);
        }

        string? toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");

        if (!string.IsNullOrEmpty(toolsPath))
        {
            string sharedPath = Path.Combine(toolsPath, @"share\raw");
            if(ScriptFileExists(sharedPath, scriptPath))
            {
                return Path.Combine(sharedPath, scriptPath);
            }
        }
        return null;
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

    private static bool ScriptFileExists(string basePath, string scriptPath)
    {
        //Log.Information("{0}", Path.Combine(basePath, scriptPath));
        //Log.Information("{0}", File.Exists(Path.Combine(basePath, scriptPath)));
        return File.Exists(Path.Combine(basePath, scriptPath));
    }

    /// <summary>
    /// Produces a human-readable standard formatted code snippet corresponding to the list of tokens provided.
    /// </summary>
    /// <param name="tokensSource">List of tokens to convert to readable format</param>
    /// <returns>A string containing the readable code for these tokens</returns>
    public static string ProduceSnippetString(List<Token> tokensSource)
    {
        // TODO: this function is gross
        StringBuilder sb = new();

        ReadOnlySpan<Token> tokenSpan = CollectionsMarshal.AsSpan(tokensSource);

        // Skip whitespace tokens that begin/end the snippet so Trim() is not required on the final string.
        int startIndex = tokenSpan[0].Type == TokenType.Whitespace ? 1 : 0;
        int endIndex = tokenSpan[^1].Type == TokenType.Whitespace ? tokenSpan.Length - 1 : tokenSpan.Length;

        for (int i = startIndex; i < endIndex; i++)
        {
            sb.Append(tokensSource[i].Lexeme);
        }

        return sb.ToString();
    }
}