# tools/field-data

All engine object-field source data, plus (from P7) the dev-time tool that converts it
into the runtime artifacts bundled in `GSCode.Workspace/Api/`. Fully repo-resident:
nothing here is read from a game install at runtime.

## sources/originals/

Verbatim copies from the game install, kept as provenance for the curated layer.
Re-imported only if a game update changes them.

- `ScriptObjectFields.xlsx` — from `%TA_TOOLS_PATH%\docs_modtools\`; 10 tabs
  (Weapons, CG DynEnt, CG Cent, Actor, Client, HudElem, Vehicle, Ent, PathNode,
  Sentient) mapping field name → engine type token (F_INT, F_FLOAT, F_STRING,
  F_LSTRING, F_VECTOR, F_WEAPON, F_ENTHANDLE, F_BYTE; 0 = untyped).
- `keys.txt` — from `%TA_TOOLS_PATH%\share\raw\radiant\`; 424 lines of map-entity KVP
  keys, format `[client] <type> <field> // comment`. The `client` prefix marks
  CSC-only keys; inline comments become hover docs.

## sources/curated/

The EDITABLE source of truth — plain diffable JSON we control, correctable and
extensible beyond stock data. Entry shape: `{ "name", "type", "readonly"? }`.

- `ai_fields.json`, `aitype_fields.json`, `entity_generic_fields.json`,
  `hudelem_fields.json`, `pathnode_fields.json`, `player_fields.json`,
  `sentient_fields.json`, `vehicle_fields.json`, `vn_fields.json` — per-entity field
  lists (relocated from the old `temp-refs/`).
- `weapon_fields_simple.json` — weapon fields; every entry carries
  `"readonly": true` (normalized from the old file's `// all read-only` comment).
- `clientfield_enums.txt` — clientfield enum reference material.

## The tool (lands in P7)

- `import` — parses `sources/originals/` into per-tab curated JSONs (adds the CSC-side
  tabs the curated layer lacks) and the parsed keys list.
- `generate` — builds `t7_object_fields.json` + `t7_radiant_keys.json` in
  `GSCode.Workspace/Api/` from `sources/curated/` alone, deterministically (stable sort).
