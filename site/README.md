# gscode-site

The GSCode website: the browsable GSC/CSC API library the extension links out to, built with
SvelteKit.

`shift+f1` in a script opens a function's page here, so the URL shape this site serves is part of the
extension's contract — see `BuiltinAtHandler` on the server side.

## Developing

```bash
npm install
npm run dev          # or: npm run dev -- --open
```

Other scripts: `build`, `preview`, `check` (svelte-check), `lint` / `format` (prettier),
`brand-assets` (regenerates the favicon, touch icons, OG card and web manifest from the mark),
`sync:api` (carries the language server's API artifacts across — see [API data](#api-data)).

Note that `lint`/`format` currently fail on every `.svelte` file with
`TypeError: getVisitorKeys is not a function` — a prettier/`prettier-plugin-svelte` version
mismatch, not a problem with any particular file. `check` is the working gate meanwhile.

The dev server defaults to port 5174 so it can run beside the assetplace on 5173.

## Design

The site follows **Datum**, the gscode design system, and shares its tokens, utilities and restyled
shadcn-svelte primitives with [gscode-assetplace](https://github.com/Blakintosh/gscode-assetplace)
(`src/css/app.css` here mirrors `src/app.css` there). Brand pieces live in `src/lib/components/site/`
(`Brush`, `HudStat`, `Logo`, `DatumMark` …); site-wide constants (URLs, extension version) in
`src/lib/data/site.ts`. Three faces, one hue, nothing round: Chakra Petch 700 uppercase for display,
Sora for prose, Cascadia Code for labels and data; teal is the only brand colour.

## API data

`src/lib/apiSource/` holds the libraries this site renders — all eight documents the extension
bundles, across five games:

| game | slug | prefix | GSC | CSC |
|---|---|---|--:|--:|
| Call of Duty 4 | `cod4` | `cod4` | 819 | — |
| World at War | `waw` | `waw` | 1,060 | 188 |
| Modern Warfare 2 | `mw2` | `mw2` | 1,111 | — |
| Black Ops | `bo1` | `bo1` | 1,377 | 320 |
| Black Ops III | `bo3` | `t7` | 2,191 | 803 |

**`server/src/GSCode.Workspace/Api/` is the source of truth.** These are copies, carried across by
`npm run sync:api` and committed so a site build never needs the server tree present. They used to be
maintained independently and drifted — both claiming revision 32 with differing bytes — so run the
sync after regenerating with `server/tools/field-data`, and `npm run sync:api -- --check` to assert
they are in step.

`src/lib/data/games.ts` mirrors the language server's `SupportedProfiles.cs`: a game gets a page only
once its artifacts ship. It also carries each library's **provenance** — whether it was built from
documentation, a mod-tools wordfile, or reconstructed from call sites, and which sibling game fills
it in. `/api/getLibrary` stamps that onto the payload so a page can say what it is: only CoD4 and
BO3 have both a complete function list and signatures verified for that game, and the other three
are shown with a banner saying so rather than presented as fact.

The extension bundles data this site still does not render — the five games' object fields and
Radiant keys.

### Routes and the URL contract

`/library/<game>/<gsc|csc>/<function>`, e.g. `/library/cod4/gsc/abs`. The pre-multi-game shapes
`/library/gsc` and `/library/gsc/<function>` are answered with a **301** to the Black Ops III
equivalent, so every link the extension, the wiki and people's bookmarks already hold keeps working.
The URL spells a game the way `gscode.game` does (`bo3`), not the way its files are named (`t7`);
`/api/getLibrary` accepts either.

Asking for a language a game does not have — `/library/cod4/csc`, since Call of Duty 4 and Modern
Warfare 2 ship no client scripts — **301**s to that game's one library, keeping the function
segment. The extension builds this URL whenever a `.csc` buffer is open under one of those two
games, and it cannot know which games have client scripts; the registry here does.

The endpoint serves `?meta=1` for the envelope alone and sets an `ETag`, so a client can ask "is
there something newer" without downloading 2.9 MB to find out.

### Payload size

The libraries total ~7.5 MB. `/api/getLibrary` loads them through `import.meta.glob`, which splits
each into its own chunk fetched on demand — importing them statically would put every byte in the
server bundle. Keep it lazy.
