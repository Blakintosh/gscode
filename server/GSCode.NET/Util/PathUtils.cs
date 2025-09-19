namespace GSCode.NET.Util;

public static class PathUtils
{
    // Normalizes file system paths, handling cases like "/c:/path" and converting separators on Windows.
    public static string NormalizeFilePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? string.Empty;

        string result = path!;

        // Handle paths that start with "/c:/" style
        if (result.Length >= 3 && result[0] == '/' && char.IsLetter(result[1]) && result[2] == ':')
        {
            result = result.Substring(1);
        }

        // Normalize directory separators for the current platform
        if (Path.DirectorySeparatorChar == '\\')
        {
            result = result.Replace('/', Path.DirectorySeparatorChar);
        }

        try
        {
            return Path.GetFullPath(result);
        }
        catch
        {
            return result;
        }
    }
}
