using System.Collections.Frozen;

namespace GSCode.Core.Diagnostics;

/// <summary>
/// The message template for every diagnostic code, kept in one table so wording
/// stays consistent and codes can never ship without a message.
/// </summary>
public static class DiagnosticMessages
{
    private static readonly FrozenDictionary<GscDiagnosticCode, string> s_templates = new Dictionary<GscDiagnosticCode, string>
    {
        // Lexing
        [GscDiagnosticCode.UnterminatedString] = "String literal is not terminated before the end of the line.",
        [GscDiagnosticCode.UnterminatedBlockComment] = "Block comment is missing its closing '*/'.",
        [GscDiagnosticCode.UnterminatedDocComment] = "Documentation block is missing its closing '@/'.",
        [GscDiagnosticCode.UnexpectedCharacter] = "Unexpected character '{0}'.",
        [GscDiagnosticCode.UnknownDirective] = "Unknown preprocessor directive '{0}'.",

        // Preprocessing
        [GscDiagnosticCode.ExpectedMacroName] = "Expected a macro name after '#define'.",
        [GscDiagnosticCode.UnterminatedMacroParameters] = "The parameter list of macro '{0}' is missing its closing ')'.",
        [GscDiagnosticCode.InvalidLineContinuation] = "A line continuation '\\' must be the last token on its line.",
        [GscDiagnosticCode.MissingInsertPath] = "Expected a file path after '#insert'.",
        [GscDiagnosticCode.InsertMissingSemicolon] = "'#insert' directive must end with ';'.",
        [GscDiagnosticCode.InvalidInsertPath] = "'{0}' is not a valid insert path: paths must be relative and cannot contain '..'.",
        [GscDiagnosticCode.InsertNotFound] = "Cannot find insert file '{0}'.",
        [GscDiagnosticCode.InsertTooDeep] = "'#insert' nesting is too deep at '{0}'.",
        [GscDiagnosticCode.InsertCycle] = "'#insert' cycle detected: '{0}' is already being inserted.",
        [GscDiagnosticCode.UnterminatedConditionalDirective] = "'{0}' directive is missing its closing '#endif'.",
        [GscDiagnosticCode.UnexpectedConditionalDirective] = "'{0}' without a matching '#if'.",
        [GscDiagnosticCode.MissingMacroArguments] = "Macro '{0}' expects an argument list.",
        [GscDiagnosticCode.UnterminatedMacroArguments] = "The argument list for macro '{0}' is missing its closing ')'.",
        [GscDiagnosticCode.InactiveConditionalBranch] = "Inactive preprocessor branch; this code is excluded from the build.",
        [GscDiagnosticCode.InsertNotAHeader] = "'{0}' is not a header; '#insert' expects a '{1}' file.",
        [GscDiagnosticCode.WrongMacroArgumentCount] = "Macro '{0}' takes {1} argument(s) but {2} were passed.",

        // Names WHERE the earlier definition is, because the reader's question is never "is this a
        // duplicate" but "which body does my call site expand to" -- and the answer is order, which
        // the source does not show when the two definitions are in different files.
        [GscDiagnosticCode.DuplicateMacroDefinition] = "'{0}' is already defined in {1}; this definition is seen later and replaces it.",
        [GscDiagnosticCode.DuplicateMacroParameter] = "Macro '{1}' already has a parameter named '{0}'; arguments passed for this one are discarded.",

        // Names the game, because the fix is either "use a file-scope constant" or "you picked the
        // wrong game", and which one it is depends on something the message cannot see.
        [GscDiagnosticCode.MacrosNotInDialect] =
            "'{0}' is not available in {1}, which has no preprocessor — macros and conditional compilation arrive in Black Ops III.",

        // Parsing
        [GscDiagnosticCode.ExpectedToken] = "Expected '{0}' but found '{1}'.",
        [GscDiagnosticCode.ExpectedDeclaration] = "Expected a function, class, or directive but found '{0}'.",
        [GscDiagnosticCode.ExpectedExpression] = "Expected an expression but found '{0}'.",
        [GscDiagnosticCode.ExpectedStatement] = "Expected a statement but found '{0}'.",
        [GscDiagnosticCode.ExpectedParameterName] = "Expected a parameter name but found '{0}'.",
        [GscDiagnosticCode.ExpectedClassMember] = "Expected 'var', 'constructor', 'destructor', or 'function' but found '{0}'.",
        [GscDiagnosticCode.ExpectedCaseLabel] = "Expected 'case' or 'default' but found '{0}'.",
        [GscDiagnosticCode.UnterminatedBlock] = "Block is missing its closing '}}'.",
        [GscDiagnosticCode.UnterminatedDevBlock] = "Dev block is missing its closing '#/'.",
        [GscDiagnosticCode.UsingAfterDeclaration] = "'#using' directives must appear before the first function or class declaration.",
        [GscDiagnosticCode.ExpectedScriptPath] = "Expected a script path after '{0}'.",
        [GscDiagnosticCode.ExpectedNamespaceName] = "Expected a namespace name after '#namespace'.",
        [GscDiagnosticCode.InvalidAssignmentTarget] = "Cannot assign to {0} — assignment needs a variable, field or array element on the left.",
        [GscDiagnosticCode.AssignmentUsedAsCondition] = "This assigns to '{0}' and tests the assigned value; '==' compares. Wrap it in parentheses if the assignment is deliberate.",
        [GscDiagnosticCode.MissingSemicolon] = "Expected ';' at the end of this statement.",
        [GscDiagnosticCode.NestingTooDeep] = "Nested too deeply to analyse; the rest of this statement was skipped.",

        // Extraction / per-file semantics
        [GscDiagnosticCode.UnknownPrecacheType] = "'{0}' is not a known #precache asset type.",
        [GscDiagnosticCode.ClientOnlyPrecacheType] = "'{0}' is a client-side asset type and can only be precached from a client script.",
        [GscDiagnosticCode.DuplicateParameter] = "Parameter '{0}' is declared more than once.",
        [GscDiagnosticCode.VarargNotLastParameter] = "'...' must be the last entry in the parameter list.",
        [GscDiagnosticCode.WrongPrecacheArgumentCount] = "#precache type '{0}' expects {1} value(s) after the type but got {2}.",
        [GscDiagnosticCode.ConstructorHasParameters] = "Constructors cannot declare parameters.",
        [GscDiagnosticCode.DestructorHasParameters] = "Destructors cannot declare parameters.",
        [GscDiagnosticCode.NonValueDefaultParameter] = "Default value for '{0}' must be a plain value (literals and vectors only).",
        [GscDiagnosticCode.DuplicateFunction] = "Function '{0}' is already defined in namespace '{1}'.",
        [GscDiagnosticCode.AmbiguousFunction] =
            "'{0}' is declared in {2} of the files this script imports, all in namespace '{1}' — which one this call reaches is undefined.",
        [GscDiagnosticCode.UnusedLocal] = "'{0}' is assigned but never used.",
        [GscDiagnosticCode.UsingNotFound] = "Cannot find script '{0}'.",
        [GscDiagnosticCode.CaseUndefined] =
            "'case undefined' never matches — a switch compares values, so this branch is unreachable.",
        [GscDiagnosticCode.NonConstantCaseLabel] = "A case label must be a constant value.",
        [GscDiagnosticCode.DuplicateCaseLabel] = "'{0}' is already a case label in this switch; only the first can ever match.",
        [GscDiagnosticCode.DuplicateImport] = "'{0}' is already imported by an earlier '{1}'.",
        [GscDiagnosticCode.VoidResultAssigned] = "'{0}' returns nothing, so this assigns undefined.",
        // {0} is the noun -- "Parameter" or "waittill output" -- since one rule covers both
        // kinds of name the author binds and never uses.
        [GscDiagnosticCode.UnusedBinding] = "{0} '{1}' is never used.",
        [GscDiagnosticCode.ClassInheritanceCycle] = "'{0}' inherits from itself through {1}.",
        // Two codes rather than one, because the RULE differs by side: a builtin is validated
        // by the engine at both bounds, while a script function accepts fewer arguments than it
        // declares (the missing ones are undefined) and is only wrong when given too many.
        [GscDiagnosticCode.TooManyArguments] = "'{0}' declares {1} parameter(s) but {2} were passed.",
        [GscDiagnosticCode.WrongBuiltinArgumentCount] = "'{0}' needs at least {1} argument(s) but {2} were passed.",

        // Cross-file / workspace semantics
        [GscDiagnosticCode.NamespaceNotImported] = "Namespace '{0}' is called but no '#using' imports a file that declares it.",
        [GscDiagnosticCode.UnusedUsing] = "'{0}' is imported but nothing from it is used.",
        [GscDiagnosticCode.UnusedInclude] = "'{0}' is included but nothing from it is used.",
        // Each names WHERE it looked, which is the only thing separating the two codes. v1's wording
        // for the script case ("The function '{0}' could not be resolved.", its FunctionDoesNotExist
        // = 3035) was kept for a long time because it says the right thing in isolation — but beside
        // the builtin message it was not distinguishable, and a reader who cannot tell which code
        // fired cannot tell whether a typo or a gap in our engine data is the likelier explanation.
        // The builtin case still avoids "could not be resolved", which reads as a tooling failure
        // when the name may simply be an engine function we have no data for.
        [GscDiagnosticCode.ScriptFunctionNotFound] = "The script function '{0}' could not be resolved; this call names a script location, so no engine function could have matched.",
        [GscDiagnosticCode.BuiltinFunctionNotFound] = "'{0}' matches no script function or known engine function.",
        // {0} is the noun -- "Parameter" or "Field" -- since the same rule covers a builtin's
        // declared-bool argument and an engine field the data types bool.
        [GscDiagnosticCode.PreferBooleanLiteral] = "{0} '{1}' is a bool; prefer '{2}' over the integer literal.",
        [GscDiagnosticCode.PrivateFunctionNotVisible] = "'{0}' is private to namespace '{1}'; only files declaring that namespace can call it.",
        [GscDiagnosticCode.ReadOnlyFieldWrite] = "Engine field '{0}' is read-only; assigning to it has no effect.",
        [GscDiagnosticCode.SizeIsReadOnly] = "'.size' is read-only and cannot be assigned.",
        [GscDiagnosticCode.DevOnlyFunctionCalledFromRelease] = "'{0}' is declared inside a '/# #/' dev block and will not exist in a release build.",
        [GscDiagnosticCode.UnreachableCode] = "Unreachable: the preceding '{0}' always leaves this block.",
        [GscDiagnosticCode.VariableNeverAssigned] = "'{0}' is read but never assigned in this function.",
        [GscDiagnosticCode.VarargOutsideVarargFunction] = "'{0}' is only bound in a function declaring '...'; add it to the parameter list to use the pack here.",
        // Names the game that DOES have the word, because the reader's next question is always
        // "since when?" -- and without an answer this reads as the tool not knowing a keyword.
        [GscDiagnosticCode.KeywordNotInDialect] = "'{0}' is not part of the {1} dialect; it arrives in {2}. Here it reads as an ordinary function name.",
        // Names the file that HAS it, which is the whole difference between this and 5014: the
        // reader's fix is one '#include' line, and the message carries the argument for it.
        [GscDiagnosticCode.FunctionNotIncluded] = "'{0}' is declared in '{1}', but this file has no '#include' bringing it into scope.",
        // Says which one runs, because "duplicate" alone leaves the reader to guess whether the
        // engine takes the first or the last -- and the answer decides whether this is a typo or
        // dead code they can delete.
        [GscDiagnosticCode.MultipleDefaultLabels] = "This switch already has a 'default' label; only the first one can ever be reached.",
        // Says WHEN it breaks, not just that it is unreliable: a threaded call with no wait in it
        // returns the right value today, so a reader told only "this is undefined" checks, sees a
        // correct value, and dismisses the rule.
        [GscDiagnosticCode.ConsumedThreadedCallResult] =
            "A 'thread' call returns at the function's first 'wait', not at its 'return' — so this reads 'undefined' as soon as the thread waits.",
        // Names what IS allowed, because "not constant" leaves the reader guessing whether
        // arithmetic counts -- and the stock scripts are full of `const AREA = 64 * 64;`.
        [GscDiagnosticCode.ExpectedConstantExpression] =
            "'{0}' is declared 'const', so its value must be known at compile time — a literal, or arithmetic over literals.",
        [GscDiagnosticCode.CannotAssignToConstant] = "'{0}' is declared 'const' and cannot be assigned to.",
        [GscDiagnosticCode.DivisionByZero] = "The divisor here is zero.",
        // Names the two things it is usually a symptom of, since the reader can see for themselves
        // that the line does nothing -- what they need is the reason it ended up that way.
        [GscDiagnosticCode.InvalidExpressionStatement] =
            "This statement computes a value and discards it, so it has no effect — a missing '=' or a call missing its '()'.",
    }.ToFrozenDictionary();

    /// <summary>Formats the template for a code with its arguments.</summary>
    public static string Format(GscDiagnosticCode code, params object[] arguments)
    {
        string template = s_templates[code];

        if ( arguments.Length == 0 )
        {
            return template;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, arguments);
    }
}
