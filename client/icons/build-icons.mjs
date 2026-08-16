// Render the Datum-brand icon set for the VS Code extension from icons/src/*.svg.
//
//   Run:  cd client && node icons/build-icons.mjs
//
// Writes icons/gscode.png, gscode-beta.png, gscode-alpha.png and
// file-{gsc,csc,gsh}.png at 256x256 with transparent backgrounds, and copies the
// three file icons into ../site/static/images/ when that folder still exists.
//
// Text is rendered by headless Chrome with the woff2 files inlined as data URIs,
// so the output does not depend on any font being installed. The two build-only
// dependencies (playwright-core, sharp) are NOT in client/package.json - the
// extension package stays clean. They are resolved from the sibling site/
// checkout, which already has both; set GSCODE_DEPS to another node_modules
// parent if yours lives elsewhere.
import { readFile, writeFile, access, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const client = resolve(here, '..');
const site = resolve(process.env.GSCODE_DEPS ?? resolve(client, '..', 'site'));
const CHROME =
	process.env.CHROME_PATH ?? String.raw`C:\Program Files\Google\Chrome\Application\chrome.exe`;
const SIZE = 256;

// Prefer a locally installed copy, fall back to the sibling site/ checkout.
async function dep(name) {
	try {
		return await import(name);
	} catch {
		const req = createRequire(join(site, 'package.json'));
		const mod = await import(pathToFileURL(req.resolve(name)).href);
		// A CommonJS package imported by file URL arrives wrapped in .default.
		return mod.chromium || mod.default ? { ...(mod.default ?? {}), ...mod } : mod;
	}
}
const { chromium } = await dep('playwright-core');
const sharp = (await dep('sharp')).default;

async function font(rel) {
	return (await readFile(join(site, 'node_modules', rel))).toString('base64');
}
const chakra = await font('@fontsource/chakra-petch/files/chakra-petch-latin-700-normal.woff2');
const mono = await font('@fontsource/ibm-plex-mono/files/ibm-plex-mono-latin-400-normal.woff2');

const src = (name) => readFile(join(here, 'src', name), 'utf8');

// The SVG is inlined into the document so its <text> picks up these @font-face
// rules; screenshotting with omitBackground keeps everything outside the artwork
// transparent.
function page(svg) {
	return `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:'Chakra Petch';font-weight:700;src:url(data:font/woff2;base64,${chakra}) format('woff2')}
@font-face{font-family:'IBM Plex Mono';font-weight:400;src:url(data:font/woff2;base64,${mono}) format('woff2')}
html,body{margin:0;padding:0;background:transparent}
body{width:${SIZE}px;height:${SIZE}px;overflow:hidden}
svg{display:block;width:${SIZE}px;height:${SIZE}px}
</style></head><body>${svg}</body></html>`;
}

const browser = await chromium.launch({ executablePath: CHROME, headless: true });
const tab = await browser.newPage({
	viewport: { width: SIZE, height: SIZE },
	deviceScaleFactor: 1
});

async function render(svgName, outName) {
	await tab.setContent(page(await src(svgName)), { waitUntil: 'load' });
	await tab.evaluate(() => document.fonts.ready);
	const buf = await tab.screenshot({ type: 'png', omitBackground: true });
	const out = join(here, outName);
	await writeFile(out, buf);
	console.log('wrote', out);
	return buf;
}

await render('icon-gscode.svg', 'gscode.png');
await render('icon-gscode-beta.svg', 'gscode-beta.png');
await render('icon-gscode-alpha.svg', 'gscode-alpha.png');

const files = {};
for (const id of ['gsc', 'csc', 'gsh']) {
	files[id] = await render(`file-${id}.svg`, `file-${id}.png`);
}

await browser.close();

// The site serves the same three file icons; only refresh them if it still does.
const siteImages = join(site, 'static', 'images');
try {
	await access(join(siteImages, 'file-gsc.png'));
	for (const [id, buf] of Object.entries(files)) {
		const out = join(siteImages, `file-${id}.png`);
		await writeFile(out, buf);
		console.log('wrote', out);
	}
} catch {
	console.log('skipped site/static/images (no file-gsc.png there)');
}

// Contact sheet: every icon at 128px and at 16px, over white and over the VS Code
// editor dark. Set ICON_SHEET to a path to write it; skipped otherwise.
if (process.env.ICON_SHEET) {
	const names = [
		'gscode.png',
		'gscode-beta.png',
		'gscode-alpha.png',
		'file-gsc.png',
		'file-csc.png',
		'file-gsh.png'
	];
	const cell = 176;
	const rowH = cell + 8;
	const width = cell * names.length;
	const bands = ['#FFFFFF', '#1E1E1E'];
	const layers = [];
	for (const [b, bg] of bands.entries()) {
		layers.push({
			input: {
				create: { width, height: rowH, channels: 4, background: bg }
			},
			top: b * rowH,
			left: 0
		});
	}
	for (const [b] of bands.entries()) {
		for (const [i, n] of names.entries()) {
			const buf = await readFile(join(here, n));
			layers.push({
				input: await sharp(buf).resize(128, 128).png().toBuffer(),
				top: b * rowH + 12,
				left: i * cell + 8
			});
			layers.push({
				input: await sharp(buf).resize(16, 16).png().toBuffer(),
				top: b * rowH + 148,
				left: i * cell + 8
			});
			layers.push({
				input: await sharp(buf).resize(16, 16).png().toBuffer(),
				top: b * rowH + 148,
				left: i * cell + 32
			});
			layers.push({
				// a 16px icon blown up nearest-neighbour, so the downscale is readable
				input: await sharp(buf)
					.resize(16, 16)
					.resize(64, 64, { kernel: 'nearest' })
					.png()
					.toBuffer(),
				top: b * rowH + 100,
				left: i * cell + 56
			});
		}
	}
	await mkdir(dirname(process.env.ICON_SHEET), { recursive: true });
	await sharp({
		create: { width, height: rowH * bands.length, channels: 4, background: '#000000' }
	})
		.composite(layers)
		.png()
		.toFile(process.env.ICON_SHEET);
	console.log('wrote', process.env.ICON_SHEET);
}
