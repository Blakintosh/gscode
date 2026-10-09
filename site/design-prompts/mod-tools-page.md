# Claude Design prompt: the Mod Tools page (Apex, Blackbird, Ultrasound)

Attach with this prompt:
- `site/src/css/app.css` (Datum, the gscode design system) and a screenshot of gscode.net's home page
- `apex/branding/apex-logo-master.png`, `blackbird/Blackbird/Assets/blackbird_256.png`, `ultrasound/branding/ultrasound-logo-master.png`
- App screenshots: `apex/shots/02-weapon-editor.png`, `apex/shots/08-compare.png`, `apex/shots/09-table.png`,
  `blackbird/Blackbird.Shots/shots/main-building.png`, `main-build-warnings.png`, `command-palette.png`

---

Design one new page for gscode.net, at `/tools`, that sells three Black Ops III Mod Tools replacements: **Apex**,
**Blackbird** and **Ultrasound**. A modder who lands here should want to install all three before reaching the
bottom. Take creative risks: this is the most visual page on the site.

## What they are

Each one replaces a stock Treyarch Mod Tools program. It installs over the original, which is kept, so nothing about
a modder's workflow changes except that it gets faster.

| | Replaces | Mascot | One-line job |
|---|---|---|---|
| **Apex** | APE, the Asset Property Editor (`asseteditor_modtools.exe`) | Black panther, cyan eye | Edits GDTs: weapons, models, materials, tables |
| **Blackbird** | The Mod Tools Launcher (`modlauncher.exe`) | SR-71-style spy plane, orange afterburners | Builds, runs and publishes maps and mods |
| **Ultrasound** | `snd_convert.exe` | Submarine sending cyan sonar arcs | Converts a map's sounds into banks during the build |

They are built by the same person, to the same rule: do what the original does, at least as well, add only what
removes weekly friction, and make it feel instant. They win on how they feel, not on feature count. That is the
pitch, so the page itself should feel fast, exact and confident, not busy.

### Apex: the asset editor that keeps up
- **Ctrl+P** opens any of 100k+ assets by name, or by filter: `type:weapon prop:damage>=100`. Type `>` for commands.
- Opening an asset takes under 200 ms; arrowing through the list never stutters.
- Your work survives a crash or restart. Unsaved changes stay on disk until you save or discard them.
- **Compare** (Ctrl+D) lines assets up side by side. **Open results as table** edits many assets at once.
- The Inspector shows what an asset uses and what uses it.
- The preview uses APE's own renderer, bit-exact, and plays xanims with notetracks.
- Every save backs up the old GDT first (last 10 per file); it never overwrites a GDT that changed on disk without asking.
- Extensions add fields and sections to existing asset types (weapon-tech's recoil editor is the first).
- Status: **preview**. Does most of what APE does; a short list of known gaps is in the README. Say so on the page.

### Blackbird: the launcher, in one window
- Launching the Mod Tools from Steam, or any shortcut you already have, opens Blackbird. Nothing to configure first.
- Window and project list up in under half a second.
- Compile, light and link with build presets; the log streams live with errors and warnings counted and filterable.
- Run in Dev or Ship launch configs, offline or through Steam.
- Publish to the Steam Workshop from inside, with several Workshop versions per project (stable and test) and a
  preflight check of title, images and build before upload.
- Create maps from Treyarch's templates, rename or duplicate projects without leaving stale names in files.
- Finds common problems: a `#using` that points nowhere, duplicate GDT assets.
- A shortcut for everything: Ctrl+K palette, Ctrl+B build, F5 build and run, Ctrl+Shift+U publish,
  Ctrl+Shift+A Asset Editor (opens Apex), Ctrl+Shift+R Radiant.
- Updates itself in the background; "Update ready" appears in the title bar.

### Ultrasound: less noise, more sound
- Written from scratch in Rust. Drop-in: the Mod Tools call it exactly as they called snd_convert.
- Measured on a map with about 1 GB of sound: **full rebuild 52.1 s → 6.5 s (8× faster)**, **no changes 19.9 s → 1.1 s
  (18× faster)**. Always show "your mileage may vary" next to these numbers.
- Fixes long-standing snd_convert bugs. Accepts non-48 kHz WAV (correctly resampled), FLAC and OGG.
- Optional lossy compression shrinks sound banks: Low ~74%, Medium ~52%, High ~36%, Extreme ~24% of original size.
  Set per project in the SZC, or per alias.
- Updates itself after a successful build, verified against the release's SHA-256.
- Status: early release, MIT licensed.

### What all three share
- One setup exe each from GitHub Releases. Windows 10/11, 64-bit, BO3 and its Mod Tools installed through Steam.
- **Never lose a Treyarch file.** The originals are backed up in a hidden `.gscode-backups` folder. Uninstall puts
  them back byte for byte. A Steam "Verify integrity" undoing the install is one click to fix.
- They work together: Blackbird's Asset Editor button opens Apex; Ultrasound runs inside every build Blackbird starts.
- Three fixed themes (Graphite, Slate, Light) that follow Windows. No theme editors, no dashboards, no settings for
  things that have a right answer.
- Independent community tools, not affiliated with Treyarch, Activision or Call of Duty. Put this in the footer area.

## Visual direction

### Function: Datum, unchanged
Use Datum (attached `app.css`) for everything structural, so the page is unmistakably gscode.net: the site header and
footer, the 12-column grid and `max-w-7xl` container, the three typefaces and their jobs (Chakra Petch 700 uppercase
for display, Sora for prose, Cascadia Code for labels and readouts), the type scale tokens, the square corners with
chamfer cuts (15 / 10 / 7 px), corner handles and the origin crosshair, numbered spreads with mono `01 / NAME · readout`
labels, `HudStrip`/`HudStat` readouts, the button variants, and the one-light-source lighting from the home page.
Light and dark both at full parity.

### Visuals: the fleet
The three mascots share one illustration style: matte black silhouettes with grey panel lines, a heavy black outline
and a single point of colour. Read together they are a stealth fleet: **land, air and sea**. Build the page's look
around that, and let each tool's section take on its own colour and motion while Datum keeps the structure.

- **Fleet palette.** The apps are neutral graphite, not gscode's blue-teal steel (see the screenshots: near-black
  `#1c1c1c`-ish grounds, Geist UI type, teal `#3ED1BD` accents). Inside each tool's section, swap the ground to that
  neutral graphite so the section feels like the app itself. Keep Datum's teal for actions so buttons stay one family.
- **Apex: the eye.** Cyan from the panther's eye is Apex's only colour. Motif: precision and focus, a predator that
  finds what it wants instantly. Ideas: the panther silhouette at huge scale, cropped, with only the eye lit; a
  Ctrl+P palette that types `type:weapon prop:damage>=100` and narrows a list of hundreds to three as you watch.
- **Blackbird: the burn.** The afterburner orange/red is the only warm colour on the whole page, so speed reads as
  Blackbird's. Motif: velocity and flight instruments. Ideas: a build log that streams line by line at real speed
  with a running error/warning count; a launch-time readout like an altimeter; the plane crossing the section on a
  diagonal, leaving a burn trail that becomes the section divider.
- **Ultrasound: the ping.** Cyan sonar arcs. Motif: waveforms, sonar rings, depth gauges. Ideas: the benchmark as a
  sonar race (two pings expanding, snd_convert's slow, Ultrasound's 8× faster, timed at the true ratio, replayable);
  a waveform that gets visibly thinner as you drag a compression slider from None to Extreme with the bank size
  updating beside it.

Use the actual mascot artwork; don't redraw or restyle it. Don't introduce gradients, glows or colours beyond Datum's
tokens plus the three mascot colours (panther cyan, afterburner orange, sonar cyan).

## Page structure (a starting point; improve on it)

1. **Hero.** The fleet in formation: panther, plane, submarine, as one composition, each silhouette catching the
   page's single light. Headline in Chakra Petch, something like **"The Mod Tools, minus the wait."** Subline: three
   drop-in replacements for Black Ops III's asset editor, launcher and sound converter; same files, same formats,
   none of the waiting. Primary CTA jumps to the install band; secondary scrolls to the first tool. A HUD strip
   underneath: `3 tools · 18× faster sound builds · <200 ms to open an asset · 0 Treyarch files lost`.

2. **The swap.** A literal `<BO3>\bin` and `<BO3>\sound` file tree. The three stock executables are replaced in turn
   by gscode's, each original sliding into `.gscode-backups`. One sentence: launch the Mod Tools exactly as before.

3. **Apex spread** (`01 / APEX · <asset count>`). Apex screenshot or live mock of the weapon editor, the Ctrl+P demo,
   and a short list of what's different from APE. Preview badge.

4. **Blackbird spread** (`02 / BLACKBIRD · <build time>`). The streaming build log mock, the shortcut table as a
   keycap grid, Workshop publish with preflight.

5. **Ultrasound spread** (`03 / ULTRASOUND · 8× / 18×`). The sonar benchmark race and the compression slider.

6. **One afternoon of modding.** A single horizontal sequence showing them working together: tune a weapon's damage
   in Apex → Ctrl+B in Blackbird → Ultrasound converts the sounds inside that build log → F5 into the game → publish
   to the Workshop. Each step labelled with its shortcut.

7. **What we won't build.** A short, confident list, styled like a spec sheet with strike-throughs: theme editors,
   stats dashboards, settings for things with a right answer, a second button for the same action. This is the
   brand's personality; it tells a modder these tools respect their time.

8. **Safe to try.** Backups, byte-for-byte uninstall, self-updating, and the honest status of each tool (Apex
   preview, Ultrasound early release). Plain and reassuring.

9. **Install band.** Three cards, one per tool, each with its mascot, version chip, what it replaces, requirements
   (Blackbird needs the .NET 10 runtime; the installer says if it's missing) and a "Download setup" button. Links:
   `github.com/Blakintosh/apex/releases`, `github.com/Blakintosh/Starlaunch/releases` (Blackbird's repo still has its
   old name), `github.com/Blakintosh/ultrasound/releases`. Note under the cards: install Blackbird first and Apex
   installs alongside APE so Blackbird opens it; otherwise Apex takes APE's place.

## Constraints

- Every claim must be true. Use only the numbers above; put versions and counts in one data object so they can be kept
  current (current: Apex preview 0.2, Ultrasound 0.3; Blackbird version to be filled in).
- Copy follows the apps' rules: sentence case for prose, no exclamation marks, no "revolutionary"/"supercharge"
  marketing voice, BO3 tool names as modders know them (GDT, SZC, zone, linker, Workshop, xanim).
- Animations run once when a section scrolls in (like the home page widgets), are replayable on click, and respect
  `prefers-reduced-motion` by showing the final state.
- Works at 375 px wide with no horizontal scroll; the fleet hero recomposes vertically on mobile.
- The page is built in SvelteKit with Tailwind v4 and Datum tokens, so design with components that map onto that:
  reuse `Eyebrow`, `HudStrip`, `HudStat`, `DatumMark`, `Button`, and add the header nav link "Tools".
