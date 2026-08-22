# T7 macro library schema

`t7_macros_gsh.json` documents the stock preprocessor macros that ship with the Black Ops III
mod tools (`#define` directives in the `.gsh` headers), plus the built-in macros the compiler
substitutes itself. It is the data source for the site's macro reference pages and mirrors the
conventions of `t7_api_gsc.json` / `t7_api_csc.json`.

## Top level

```json
{
  "gameId": "t7",
  "languageId": "gsh",
  "revisedOn": "<ISO 8601 timestamp>",
  "revision": 1,
  "macros": []
}
```

`macros` is kept sorted by `name`, case-insensitive. Macros are shared between GSC and CSC
(a `.gsh` can be inserted into either), so there is one file, not one per language.

## Macro entry

```json
{
  "name": "SPAWNFLAG_TRIGGER_SPAWN",
  "kind": "constant",
  "description": "Spawn flag that marks a trigger as a spawn trigger.",
  "definitions": [
    {
      "path": "scripts/shared/shared.gsh",
      "line": 33,
      "parameters": null,
      "expansion": "32"
    }
  ],
  "example": null,
  "remarks": null,
  "flags": ["generated"],
  "confidence": "high"
}
```

Fields:

- `name` (string, required): the macro's exact spelling. Macro names are case-sensitive in
  script, unlike function names.
- `kind` (required): one of
  - `"constant"`: an object-like macro; expands wherever the bare name is written.
  - `"function"`: a function-like macro; takes arguments in parentheses.
  - `"builtin"`: substituted by the compiler itself; no `.gsh` defines it.
- `description` (string, required): one to three sentences. Follow the API house style:
  sentence case, ends with a period, third person ("Returns...", "Marks...", "The maximum...").
  Plain ASCII only. No em dashes; use commas, parentheses, or separate sentences instead.
- `definitions` (array, required; empty for builtins): every stock `.gsh` that defines the
  name. Each definition has:
  - `path`: forward-slash path relative to the mod tools script root, e.g.
    `scripts/shared/shared.gsh`.
  - `line`: 1-based line of the `#define`.
  - `parameters`: for `function` macros, an array of `{ "name", "description" }` in
    declaration order (macro parameters are untyped and all required); `null` for constants.
  - `expansion`: the macro body exactly as defined, with whitespace collapsed, continuation
    backslashes removed, and any trailing `//` comment stripped. May be `""` for flag-style
    empty defines.
- `example` (string or null): a short usage snippet in stock style, ideally lifted or adapted
  from real corpus usage, e.g. `if ( level.gamemode == GAMEMODE_PRIVATE_MATCH )`.
- `remarks` (string or null): quirks worth a caller's attention: values that must stay in sync
  with engine enums, mp/zm variants that expand differently, side effects of evaluating the
  expansion (dvar reads, function calls), related macros. Omit when there is nothing
  non-obvious to say.
- `flags` (array of strings): provenance markers; `"generated"` for bulk-authored entries,
  `"verified"` once a human has checked one.
- `confidence`: `"high"` when definition and usage make the meaning unambiguous, `"medium"`
  when inferred mostly from naming, `"low"` when genuinely unclear (say why in `remarks`).

When the same name expands differently per context (for example an mp and a zm header both
define it), keep one entry, list every definition, and cover the difference in `remarks`.

## Appending

Use `append_macro.py` rather than editing the JSON by hand:

```
python3 append_macro.py t7_macros_gsh.json --entry entry.json     # one entry
python3 append_macro.py t7_macros_gsh.json --bulk fragment.json   # a JSON array of entries
```

The script validates each entry against this schema, merges `definitions` when the name
already exists, keeps the array alphabetised, and bumps `revisedOn`/`revision`.
