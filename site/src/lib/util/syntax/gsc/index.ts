import gsc from './gsc.tmGrammar.json';
import { createHighlighter, type ThemeRegistration } from 'shiki';
import { datumDark, datumLight } from './datum-theme';

async function initHighlighter() {
	return await createHighlighter({
		langs: [gsc as any],
		themes: [datumDark as ThemeRegistration, datumLight as ThemeRegistration]
	});
}

/** Shared highlighter promise — one instance per server/client. */
export default initHighlighter();
