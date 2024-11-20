# GSCode

A Visual Studio Code language extension that provides IntelliSense support for Call of Duty: Black Ops III's scripting languages, GSC and CSC.

GSCode helps you to find and fix errors before the compiler has to tell you, streamlining scripting. In its current preview version, language support is provided up to syntactic analysis, allowing you to see syntax errors in your code. It also supports the preprocessor, meaning you can see macro usages in your code and spot preprocessor errors.

In the future, full semantic analysis of script files is planned, allowing you to see an entire extra class of errors caught at compile-time or run-time. Additionally, this will provide richer IntelliSense to your editor.

## Requirements

GSCode's language server requires the .NET 8 Runtime, available at [Download .NET 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). **You do not need the SDK.**

## Release Notes

### 0.1.1 beta (latest)

Minor tweaks and fixes.

### 0.1.0 beta

Initial public release. Adds GSC & CSC language support, providing syntax highlighting and IntelliSense for preprocessor and syntactic analysis.

## Reporting Issues and Tweaks

As GSCode is an indepedent implementation of a GSC language parser, it may not immediately have feature parity with the GSC compiler. This is the eventual aim, however. More specifically, we aim to ensure that GSCode catches any and all errors caught by the GSC compiler as a minimum (syntactic and semantic).

With that in mind, if you encounter any situations where the GSC compiler (Linker) reports a syntax error, but GSCode does not, this constitutes an issue. You can report these issues to the [issue tracker on GitHub](https://github.com/Blakintosh/gscode/issues); please provide the expected error and attach a script that can reproduce the issue. Issues reporting bugs in isolated script cases without attaching a script (snippet) will not be looked into!

## Known Issues

* Macro hoverables only show nested macro expansions if nested macros are not at the start or end of the expansion.

## Licence
GSCode is open-source software licenced under the GNU General Public License v3.0. 

```
GSCode - Black Ops III GSC Language Extension
Copyright (C) 2024 Blakintosh

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