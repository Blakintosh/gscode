# GSCode

A Visual Studio Code language extension that provides IntelliSense support for Call of Duty: Black Ops III's scripting languages, GSC and CSC.

GSCode helps you to find and fix errors before the compiler has to tell you, streamlining scripting. In its current beta version, language support is provided up to syntactic analysis, allowing you to see syntax errors in your code. It also supports the preprocessor, meaning you can see macro usages in your code and spot preprocessor errors.

In the future, full semantic analysis of script files is planned, allowing you to see an entire extra class of errors caught at compile-time or run-time. Additionally, this will provide richer IntelliSense to your editor.

## Requirements

GSCode's language server requires the .NET 8 Runtime, available at [Download .NET 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). **You do not need the SDK.**

## Release Notes

### 1.0 (latest)

- Adds semantic analysis steps & type inference associated validation.
- Various bug fixes.
- End of beta phase.

### 0.10 beta

- Disabled workspace indexing temporarily due to performance concerns.
- Added reference finding (Go to Reference, Find All References)
- Added workspace indexing of scripts.
- Fixed switch case analysis with braced bodies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the above changes ([#30](https://github.com/Blakintosh/gscode/pull/30), [#31](https://github.com/Blakintosh/gscode/pull/31)).

### 0.9 beta

- Added Outliner support for classes, functions, and macros.
- Added goto definition support for usings, script functions, and macros.
- Added signature support for script functions & builtins.
- Fixed function & variable names not showing signatures & tooltips due to case-sensitivity.
- Added analyser checks for: unknown namespace, unused using, unused variable, unused parameters, switch checks.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the above changes ([#24](https://github.com/Blakintosh/gscode/pull/24)).

Additionally,

- Added comment code region support (`/* region Name */` `/* endregion */` syntax) with folding ranges in the editor ([#22](https://github.com/Blakintosh/gscode/issues/22)).

### 0.2 beta

- Added a 'dumb' completion handler to suggest function completions.
- Added a 'dumb' handler to provide GSCode API hover documentation on built-in functions.
- Added diagnostic for missing scripts from using.
- Added basic signature analysis for highlighting of class, function, method and parameter definitions.
- Added using highlight with script path hint.
- Various bug fixes.

### 0.1 beta

- Initial public release. Adds GSC & CSC language support, providing syntax highlighting and IntelliSense for preprocessor and syntactic analysis.

## Reporting Issues and Tweaks

As GSCode is an indepedent implementation of a GSC language parser, it may not immediately have feature parity with the GSC compiler. Any instance where it does not catch bugs that the GSC compiler does will be considered a bug. Additionally, we're hoping to catch more bugs than the GSC compiler eventually.

With that in mind, if you encounter any situations where the GSC compiler (Linker) reports a syntax error, but GSCode does not, this constitutes an issue. You can report these issues to the [issue tracker on GitHub](https://github.com/Blakintosh/gscode/issues); please provide the expected error and attach a script that can reproduce the issue. Issues reporting bugs in isolated script cases without attaching a script (snippet) will not be looked into!

## Known Issues

- Macro hoverables only show nested macro expansions if nested macros are not at the start or end of the expansion.

## Licence

GSCode is open-source software licenced under the GNU General Public License v3.0.

```
GSCode - Black Ops III GSC Language Extension
Copyright (C) 2025 Blakintosh

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```

Please see [LICENSE.md](https://github.com/Blakintosh/gscode/blob/main/LICENSE.md) for details.
