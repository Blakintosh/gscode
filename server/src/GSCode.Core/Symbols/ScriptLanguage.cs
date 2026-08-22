namespace GSCode.Core.Symbols;

/// <summary>Which script world a file belongs to. GSC and CSC never see each other; GSH serves both.</summary>
public enum ScriptLanguage
{
    Gsc,
    Csc,
    Gsh,
}
