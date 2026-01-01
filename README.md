# GSCode

A Visual Studio Code language extension that provides IntelliSense support for Call of Duty: Black Ops III's scripting languages, GSC and CSC.

This repository contains the source code for the GSCode language server, which uses the [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) to provide IntelliSense support for GSC and CSC. Additionally, it contains the source code for the GSCode VSCode extension, which is a language client that communicates with the language server to provide the actual IntelliSense.

For the latest information on GSCode's releases and features, please see [README.md](https://github.com/Blakintosh/gscode/blob/main/client/README.md) of the client directory.

## Reporting Issues and Tweaks

As GSCode is an indepedent implementation of a GSC language parser, it may not immediately have feature parity with the GSC compiler. However, we're aiming for it to catch all errors that are typically caught at compile-time, and additionally, a wide range of errors previously only caught at runtime.

With that in mind, if you encounter any situations where the GSC compiler (Linker) reports an error, but GSCode does not, this constitutes an issue. You can report these issues to the [issue tracker on GitHub](https://github.com/Blakintosh/gscode/issues); please provide the expected error and attach a script that can reproduce the issue. Issues reporting bugs in isolated script cases without attaching a script (snippet) will not be looked into!

## Licence

GSCode is open-source software licenced under the GNU General Public License v3.0.

```
GSCode - Black Ops III GSC Language Extension
Copyright (C) 2026 Blakintosh

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
