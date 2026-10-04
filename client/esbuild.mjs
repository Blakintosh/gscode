// Builds the extension that ships: one bundled out/extension.js, with no node_modules beside it.
//
// Packaging used to copy tsc's output and let vsce collect node_modules. That shipped whatever was
// installed, not what the extension uses: the 2.0.0 VSIX carried @vscode/test-electron, jszip, ora
// and their dependencies, around 400 files that nothing requires. It also shipped every stale .js
// left in out/ by a source file that no longer exists, because tsc never deletes anything.
//
// So out/ is emptied first, and esbuild follows the imports from src/extension.ts: what is reachable
// is in the bundle, and nothing else is. `vscode` stays external because the host provides it.
//
// The F5 loop does not use this. It runs `tsc -watch` into the same out/ folder, which is what the
// launch configs and their source maps expect.
import * as esbuild from "esbuild";
import * as fs from "fs";

fs.rmSync("out", { recursive: true, force: true });

await esbuild.build({
    entryPoints: ["src/extension.ts"],
    outfile: "out/extension.js",
    bundle: true,
    platform: "node",
    format: "cjs",
    // VS Code 1.85, the oldest the manifest accepts, runs Node 18.
    target: "node18",
    external: ["vscode"],
    logLevel: "info",
});
