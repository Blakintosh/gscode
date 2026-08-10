---
name: regenerate-game-data
description: Regenerate the bundled builtin-API, object-field and radiant-key data with tools/field-data. Use when adding builtins, importing a game's wordfile or docs, or changing anything under src/GSCode.Workspace/Api — it covers the source layers, the ASCII rule, and what a regeneration must not silently drop.
---

# Regenerating the bundled game data

The generator is `server/tools/field-data`. Its own `FOLDER.md` is the reference; this is the
working procedure and the traps.

## Source layers, in precedence order

1. **A documented page** — the richest source (signatures, descriptions, examples).
2. **A curated reconstruction** — hand-written, under `sources/curated/`.
3. **An inherited sibling** — e.g. BO1 taking CoD4's entry for a shared function.
4. **A bare name** — from a wordfile, with no signature.

Later layers never overwrite earlier ones. A regeneration that loses detail means the precedence
was applied the wrong way round.

## Output conventions

**ASCII only.** The artifacts are written with the relaxed JSON encoder and must contain no
non-ASCII bytes — the T7 files are the reference for the shape. A smart quote arriving from a
documentation page is the usual cause.

Every generated file is committed. They are build inputs, hashed into `ServerBuildIdentity`, so a
change to any of them invalidates every workspace cache — which is correct, and worth knowing
before regenerating casually.

## After regenerating, check the file set

`src/GSCode.Workspace/Api/*.json` and `*.txt` are copied to the build output by a **wildcard** in
`GSCode.Workspace.csproj`. They were once listed individually, and a new file that nobody added to
the list simply never reached the test output — which presented as BO1 reporting zero missing
builtins when the true number was 529.

Confirm the counts moved the way you expected, and that no file lost entries.

## Provenance belongs in the file

Generated artifacts carry a header comment naming where the data came from. `weapon_fields_simple.json`
is the example: its read-only flags cite `Weapon Fields.txt` explicitly, because an earlier import
applied 362 read-only flags **by hand** and the resulting lint reported 87 warnings on shipped code.
If a fact cannot be cited, it should not be generated.

## Environment

- `GSCODE_COD4_DOCS` — the CoD4 documentation pages, when regenerating CoD4's API. Unset means the
  wordfile names alone, which is a valid state: a regeneration without the docs never silently
  loses detail, it just stops gaining it.
- The `GSCODE_CORPUS_*` variables belong to the tests, not to this tool.

## Never copy third-party sources into the repo

Documentation used as a source is read from wherever it lives on disk, exactly as the corpus tests
read game scripts. Import the derived data, not the source.
