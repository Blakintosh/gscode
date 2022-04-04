# GSCode (also just known as gsc)

This is the README for your extension "gsc". After writing up a brief description, we recommend including the following sections.

## TODO
### Syntax Highlighting
* `#if`, `#elif`, `#else`, `#endif` etc.
* `/# #/`

### Semantic Highlighting Ideas
* Function calls are unhighlighted when a definition for that call cannot be found, and the function is not built-in
* References to function parameters in the body of the function are given a light blue highlight
* Waittill, foreach, for, etc. parameters are given a blue highlight like function parameters and further references do the same as above
* Preprocessor references are unhighlighted if they cannot be found
* Special highlight for parameters, functions that are never used
* ...

### IntelliSense
* Language server that is capable of grabbing the existence of functions and classes, references within specific scopes. Need to be able to determine syntax errors also, missing {} etc.
* Auto complete
* Documentation via `/@ @/` including a generate autocomplete, fallback to `/* */` or `//` before the function if no documentation found.
* Number of references before functions, maybe?
* ...

## Features

Describe specific features of your extension including screenshots of your extension in action. Image paths are relative to this README file.

For example if there is an image subfolder under your extension project workspace:

\!\[feature X\]\(images/feature-x.png\)

> Tip: Many popular extensions utilize animations. This is an excellent way to show off your extension! We recommend short, focused animations that are easy to follow.

## Requirements

n/a

## Extension Settings

Include if your extension adds any VS Code settings through the `contributes.configuration` extension point.

For example:

This extension contributes the following settings:

* `myExtension.enable`: enable/disable this extension
* `myExtension.thing`: set to `blah` to do something

## Known Issues

Calling out known issues can help limit users opening duplicate issues against your extension.

## Release Notes

Users appreciate release notes as you update your extension.

### 1.0.0

Initial release of ...

### 1.0.1

Fixed issue #.

### 1.1.0

Added features X, Y, and Z.

-----------------------------------------------------------------------------------------------------------
## Following extension guidelines

Ensure that you've read through the extensions guidelines and follow the best practices for creating your extension.

* [Extension Guidelines](https://code.visualstudio.com/api/references/extension-guidelines)

## Working with Markdown

**Note:** You can author your README using Visual Studio Code.  Here are some useful editor keyboard shortcuts:

* Split the editor (`Cmd+\` on macOS or `Ctrl+\` on Windows and Linux)
* Toggle preview (`Shift+CMD+V` on macOS or `Shift+Ctrl+V` on Windows and Linux)
* Press `Ctrl+Space` (Windows, Linux) or `Cmd+Space` (macOS) to see a list of Markdown snippets

### For more information

* [Visual Studio Code's Markdown Support](http://code.visualstudio.com/docs/languages/markdown)
* [Markdown Syntax Reference](https://help.github.com/articles/markdown-basics/)

**Enjoy!**
