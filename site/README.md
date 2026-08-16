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
`brand-assets` (regenerates the favicon, touch icons, OG card and web manifest from the mark).

The dev server defaults to port 5174 so it can run beside the assetplace on 5173.

## Design

The site follows **Datum**, the gscode design system, and shares its tokens, utilities and restyled
shadcn-svelte primitives with [gscode-assetplace](https://github.com/Blakintosh/gscode-assetplace)
(`src/css/app.css` here mirrors `src/app.css` there). Brand pieces live in `src/lib/components/site/`
(`Brush`, `HudStat`, `Logo`, `DatumMark` …); site-wide constants (URLs, extension version) in
`src/lib/data/site.ts`. Three faces, one hue, nothing round: Chakra Petch 700 uppercase for display,
Sora for prose, IBM Plex Mono for labels and data; teal is the only brand colour.

## API data

`src/lib/apiSource/` holds the library this site renders — `t7_api_gsc.json` and `t7_api_csc.json`,
the same documents the extension bundles in `server/src/GSCode.Workspace/Api/`.

They are **copies, not a shared source**: both live in this repo but neither reads the other's, so a
correction made on one side has to be carried across deliberately. The extension also bundles data
this site does not render — the four pre-BO3 games' libraries, object fields and Radiant keys.
Sharing the generated artifacts is tracked in `server/FOLLOWUPS.md`.
