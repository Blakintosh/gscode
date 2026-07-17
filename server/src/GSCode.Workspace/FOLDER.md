# GSCode.Workspace

Workspace layer: the script database (separate GSC/CSC stores), path/mod-overlay
resolution, background indexing, the SQLite cache, and the bundled game data. LSP-free.

*(Code lands from P5 on — P0 ships only the bundled data below.)*

## Api/

Bundled game data, copied to the build output and loaded at runtime:

- `t7_api_gsc.json` — builtin (engine) function library for GSC: names, overloads,
  parameters, descriptions. Builtins are namespace-less in v2.
- `t7_api_csc.json` — same, for CSC.
- `t7_stock_scripts.txt` — list of script files that shipped with the mod tools;
  powers the `rawFileWarningMode = "stock"` save warning (P5).
