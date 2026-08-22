// Generate the Datum brand rasters for gscode.net: static/og.png (1200×630),
// static/apple-touch-icon.png (180), static/icon-192.png / icon-512.png, static/favicon.ico
// (16 + 32) and static/site.webmanifest, all from the SVG mark.
// Run: npm run brand-assets   (needs Chrome for the OG card; sharp does the icons)
//
// Sibling of gscode-assetplace/scripts/brand-assets.mjs — keep the two in step.
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright-core';
import sharp from 'sharp';

const CHROME =
	process.env.CHROME_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

const mark = await readFile('src/lib/assets/datum-mark.svg', 'utf8');
const markB64 = Buffer.from(mark).toString('base64');

// Below 16px the reference line can't survive: the rule is the plain chamfered square.
const markPlain = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 26 26"><defs><linearGradient id="g" x1="0" y1="0" x2="0.62" y2="0.79"><stop offset="0" stop-color="#7BEFDD"/><stop offset="0.58" stop-color="#3ED1BD"/><stop offset="1" stop-color="#8B7BFF"/></linearGradient></defs><path fill="url(#g)" d="M8 0 H26 V18 L18 26 H0 V8 Z"/></svg>`;

async function fontData(path) {
	return (await readFile(path)).toString('base64');
}
const chakra = await fontData(
	'node_modules/@fontsource/chakra-petch/files/chakra-petch-latin-700-normal.woff2'
);
const sora = await fontData('node_modules/@fontsource-variable/sora/files/sora-latin-wght-normal.woff2');
const mono = await fontData(
	'node_modules/@fontsource-variable/cascadia-code/files/cascadia-code-latin-wght-normal.woff2'
);

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:'Chakra Petch';font-weight:700;src:url(data:font/woff2;base64,${chakra}) format('woff2')}
@font-face{font-family:'Sora';font-weight:100 800;src:url(data:font/woff2;base64,${sora}) format('woff2')}
@font-face{font-family:'Cascadia Code';font-weight:100 900;src:url(data:font/woff2;base64,${mono}) format('woff2')}
html,body{margin:0;width:1200px;height:630px;overflow:hidden}
body{background:#07080C;color:#E2EAEC;font-family:'Sora',sans-serif;position:relative}
.grid{position:absolute;inset:0;background-image:
 linear-gradient(#111A20 1px,transparent 1px),linear-gradient(90deg,#111A20 1px,transparent 1px),
 linear-gradient(#1A2A33 1px,transparent 1px),linear-gradient(90deg,#1A2A33 1px,transparent 1px);
 background-size:16px 16px,16px 16px,128px 128px,128px 128px}
.lit{position:absolute;inset:0;background-image:
 linear-gradient(rgba(62,209,189,.30) 1px,transparent 1px),linear-gradient(90deg,rgba(62,209,189,.30) 1px,transparent 1px),
 linear-gradient(rgba(123,239,221,.44) 1px,transparent 1px),linear-gradient(90deg,rgba(123,239,221,.44) 1px,transparent 1px);
 background-size:16px 16px,16px 16px,128px 128px,128px 128px;
 -webkit-mask-image:radial-gradient(58% 80% at 78% -8%,#000 0,rgba(0,0,0,.42) 38%,transparent 74%)}
.glow{position:absolute;inset:0;background:radial-gradient(44% 52% at 80% -6%,rgba(123,239,221,.20),rgba(139,123,255,.09) 52%,transparent 74%)}
.origin{position:absolute;left:72px;top:64px;color:#3ED1BD;font-family:'Cascadia Code';font-size:14px;letter-spacing:.12em}
.origin i{position:absolute;background:#3ED1BD;display:block}
.origin .h{width:44px;height:1px;left:0;top:0}.origin .v{width:1px;height:44px;left:0;top:0}
.origin span{position:absolute;left:52px;top:-9px;white-space:nowrap}
.wrap{position:absolute;left:72px;right:72px;bottom:72px}
.word{display:flex;align-items:center;gap:22px;font-family:'Chakra Petch';font-weight:700;font-size:64px;letter-spacing:.03em;text-transform:uppercase;line-height:1}
.word img{width:60px;height:60px;display:block}
.strap{margin-top:34px;font-family:'Chakra Petch';font-weight:700;text-transform:uppercase;font-size:44px;letter-spacing:.005em;line-height:1;max-width:19ch}
.strap em{font-style:normal;background:linear-gradient(94deg,#7BEFDD,#3ED1BD 55%,#8B7BFF);-webkit-background-clip:text;background-clip:text;color:transparent}
.lede{margin-top:20px;font-weight:300;font-size:22px;color:#8B9BA3;max-width:46ch;line-height:1.5}
.readout{position:absolute;right:72px;bottom:72px;font-family:'Cascadia Code';font-size:14px;letter-spacing:.14em;text-transform:uppercase;color:#5A6C76}
.readout b{color:#3ED1BD;font-weight:400}
.handle{position:absolute;width:11px;height:11px;border:2px solid #3E5661;background:#07080C}
</style></head><body>
<div class="grid"></div><div class="lit"></div><div class="glow"></div>
<div class="origin"><i class="h"></i><i class="v"></i><span>0, 0</span></div>
<div class="handle" style="right:28px;top:28px;border-color:#3ED1BD"></div>
<div class="handle" style="left:28px;bottom:28px"></div>
<div class="wrap">
  <div class="word"><img src="data:image/svg+xml;base64,${markB64}" alt="">gscode</div>
  <div class="strap">IDE tooling for<br>Call of Duty<br><em>scripting</em></div>
  <div class="lede">A language server for Call of Duty GSC and CSC, compatible with all VS Code-based IDEs.</div>
</div>
<div class="readout">gscode.net · <b>vs code</b></div>
</body></html>`;

const browser = await chromium.launch({ executablePath: CHROME, headless: true });
const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
await page.setContent(html, { waitUntil: 'load' });
await page.evaluate(() => document.fonts.ready);
await page.screenshot({ path: 'static/og.png', type: 'png' });
await browser.close();
console.log('wrote static/og.png');

// Icons from the mark on ground. Pad the mark to keep the clearspace (8/26 of the mark).
async function iconBuffer(size, svg = mark, pad = true) {
	const inner = pad ? Math.round(size * (26 / 42)) : size;
	const markPng = await sharp(Buffer.from(svg)).resize(inner, inner).png().toBuffer();
	return sharp({ create: { width: size, height: size, channels: 4, background: '#07080C' } })
		.composite([{ input: markPng, gravity: 'centre' }])
		.png()
		.toBuffer();
}
async function icon(size, out) {
	await writeFile(out, await iconBuffer(size));
	console.log('wrote', out);
}
await icon(180, 'static/apple-touch-icon.png');
await icon(192, 'static/icon-192.png');
await icon(512, 'static/icon-512.png');

// favicon.ico: PNG-in-ICO container, 16 (plain square, no cut line) + 32 (full mark).
function ico(pngs) {
	const header = Buffer.alloc(6);
	header.writeUInt16LE(0, 0);
	header.writeUInt16LE(1, 2);
	header.writeUInt16LE(pngs.length, 4);
	const dir = Buffer.alloc(16 * pngs.length);
	let offset = 6 + dir.length;
	pngs.forEach(({ size, buf }, i) => {
		const o = i * 16;
		dir.writeUInt8(size >= 256 ? 0 : size, o);
		dir.writeUInt8(size >= 256 ? 0 : size, o + 1);
		dir.writeUInt8(0, o + 2);
		dir.writeUInt8(0, o + 3);
		dir.writeUInt16LE(1, o + 4);
		dir.writeUInt16LE(32, o + 6);
		dir.writeUInt32LE(buf.length, o + 8);
		dir.writeUInt32LE(offset, o + 12);
		offset += buf.length;
	});
	return Buffer.concat([header, dir, ...pngs.map((p) => p.buf)]);
}
await writeFile(
	'static/favicon.ico',
	ico([
		{ size: 16, buf: await iconBuffer(16, markPlain, false) },
		{ size: 32, buf: await iconBuffer(32, mark, false) }
	])
);
console.log('wrote static/favicon.ico');

await writeFile(
	'static/site.webmanifest',
	JSON.stringify(
		{
			name: 'gscode',
			short_name: 'gscode',
			start_url: '/',
			display: 'browser',
			background_color: '#07080C',
			theme_color: '#07080C',
			icons: [
				{ src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
				{ src: '/icon-512.png', sizes: '512x512', type: 'image/png' }
			]
		},
		null,
		'\t'
	) + '\n'
);
console.log('wrote static/site.webmanifest');
