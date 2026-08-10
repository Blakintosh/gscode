namespace GSCode.Core.Diagnostics;

/// <summary>Editor presentation hint for a diagnostic. Values match the LSP wire encoding so mapping is a cast.</summary>
public enum DiagnosticTag
{
    /// <summary>Greys the range out; used for excluded #if branches and unused #using directives.</summary>
    Unnecessary = 1,

    /// <summary>Strikes the range through.</summary>
    Deprecated = 2,
}
