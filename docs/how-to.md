# How-to — recipes for common changes

The step-by-step procedures already exist, as **skills** in `.claude/skills/<name>/SKILL.md`. They
are plain Markdown, written for any engineer (and read automatically by Claude Code). This page is
the index; follow the link rather than a summary, because the skill is what gets updated.

| I want to… | Read | It covers |
|---|---|---|
| Build, run tests, run a corpus sweep | [build-and-test](../.claude/skills/build-and-test/SKILL.md) | per-project builds, the DLL lock, the corpus no-op trap, the batch files |
| Add a diagnostic, or change one's severity | [add-diagnostic](../.claude/skills/add-diagnostic/SKILL.md) | code ranges, the message table, where the rule belongs, severity by measurement, the sweep that must pass |
| Add or change an LSP feature | [lsp-handler](../.claude/skills/lsp-handler/SKILL.md) | stale analysis, `ResolveFresh` vs `Resolve`, the shared reference query, failures only a live editor shows |
| Support another game, or change a dialect | [add-game-profile](../.claude/skills/add-game-profile/SKILL.md) | the `GameProfile` seam, the verification bar, BO3-shaped assumptions |
| Add builtins or engine fields | [regenerate-game-data](../.claude/skills/regenerate-game-data/SKILL.md) | the source layers, the ASCII rule, what a regeneration must not drop |
| Write a rule about variables, arrays, scope or names | [gsc-dialect-facts](../.claude/skills/gsc-dialect-facts/SKILL.md) | language facts every earlier wrong lint learned |
| Investigate a bug report | [verify-before-fixing](../.claude/skills/verify-before-fixing/SKILL.md) | reproducing first, and proving the fix |
| Write or clean up a test | [standard-tests](../.claude/skills/standard-tests/SKILL.md) | the shared harnesses, when to name a game or a real path |
| Simplify or consolidate an area | [simplify-pass](../.claude/skills/simplify-pass/SKILL.md) | what counts as duplication here, what to leave alone, verification |

## Short recipes without a skill

**Add a setting.**
1. Declare it in `client/package.json` (`contributes.configuration`) with a description.
2. Add it to the payload in `client/src/settings.ts` (`GscodeSettings` and `readSettings`).
3. Add a property to `ServerSettings` and read it in `Apply`.
4. Read it where it applies. If it must take effect live, handle it in `ConfigurationHandler`;
   otherwise it needs a reload, and `client/src/reloadPrompt.ts` should offer one.
5. `SettingsReachTheServerTests` fails if step 2 is missing. Add the row to
   [features.md](features.md#settings) and document it in `client/README.md`.

**Add a client ↔ server message.**
1. Server, for a request: a params class carrying `[Method("gscode/<name>", Direction.ClientToServer)]`
   and implementing `IRequest<TResponse>`, plus a handler implementing
   `IJsonRpcRequestHandler<TParams, TResponse>` (`ClearCacheHandler` is a small example), registered
   with `AddHandler<T>()` in `ServerServices.Configure`. For a notification:
   `ILanguageServerFacade.SendNotification("gscode/<name>", payload)`, through
   `ConnectionSettleGate` if it can be sent during startup.
2. Client: `sendRequest` or `onNotification` in `client/src/extension.ts`.
3. Payloads are records with primitive fields, so no serializer can rename a key.
4. Add the row to [features.md](features.md#custom-protocol-messages-gscode).

**Add a field to `ScriptRecord`.**
1. Add it to the record and fill it in `ScriptDatabase.BuildRecord`.
2. Add it to both `RecordSerializer.Serialize` and `Deserialize`, in the same position.
3. Bump `CacheSchema.RecordFormatVersion`. Every user's cache is wiped on next start, which is
   the intended effect.
4. If a query needs it across files, add an index to `LanguageStore` and include it in
   `ApplyIndexes`.

**Answer a new cross-file question.** Write it as a `DatabaseQueries` method. If it needs "every
file that …", add an inverted index (usually a `PackedInvertedIndex<TKey>`) to `LanguageStore`,
built in `Contributions.Of` and diffed in `ApplyIndexes`. Keep the walk it replaces as a reference
implementation in a test, as the `*IndexTests` classes do.

**Record a performance change.** Measure with `scripts\perf.bat` (or `scale.bat`) before and after
in Release, and add a dated entry to the relevant section of `server/PERF.md` with both numbers,
including when the result is "not worth it".

**Record an open question.** Add it to `server/FOLLOWUPS.md` under Backlog, with what is known and
what would decide it. Nowhere else.
