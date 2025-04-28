import fs from 'fs';
import gsc from "./gsc.tmGrammar.json";
import { createHighlighter } from 'shiki';

// Wrap the highlighter creation in an async function
async function initHighlighter() {
    return await createHighlighter({
        langs: [gsc as any],
        themes: ["light-plus", "dark-plus"],
    });
}

// Export the promise
export default initHighlighter();