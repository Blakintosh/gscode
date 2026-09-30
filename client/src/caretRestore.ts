import * as vscode from "vscode";
import type { Middleware } from "vscode-languageclient/node";

/**
 * Keeps the caret where it was across Format Document (and format-on-save and Format Selection).
 *
 * The server returns local edits, one small edit per changed spot. But VS Code's text buffer, handed
 * a thousand edits or more, stops applying them one by one and replaces the whole span from the
 * first to the last in one operation — and a caret inside that span lands at its end. VS Code also
 * splits a formatter's edits into smaller ones before applying them, so reformatting a large file
 * always crosses that line, however local the server's edits are: on a 7,000-line script the caret
 * went from line 2399 to line 7284.
 *
 * So the caret's destination is worked out here, from the same edits, before they are applied: as a
 * character offset in the document, shifted by how much every edit before it grows or shrinks the
 * text. Once VS Code has applied them, the caret is put back at that offset and revealed.
 */

/** Where an offset in the document ends up once the edits are applied. */
export function mapOffset(document: vscode.TextDocument, offset: number, edits: readonly vscode.TextEdit[]): number {
    const sorted = [...edits].sort((a, b) => a.range.start.compareTo(b.range.start));
    let shift = 0;
    for (const edit of sorted) {
        const start = document.offsetAt(edit.range.start);
        const end = document.offsetAt(edit.range.end);
        if (start > offset) {
            break;
        }

        // An edit containing the offset takes it to the end of what replaced it, as VS Code does.
        if (end > offset) {
            return start + shift + edit.newText.length;
        }

        shift += edit.newText.length - (end - start);
    }

    return offset + shift;
}

/**
 * Remembers every caret in the document's editor, and once the edits have been applied, puts each
 * back where its offset went. Does nothing if the document is not the active editor's, or if no
 * change arrives (a cancelled or empty format).
 */
function restoreCaretsAfter(document: vscode.TextDocument, edits: readonly vscode.TextEdit[]): void {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document !== document || edits.length === 0) {
        return;
    }

    const targets = editor.selections.map((selection) => ({
        anchor: mapOffset(document, document.offsetAt(selection.anchor), edits),
        active: mapOffset(document, document.offsetAt(selection.active), edits),
    }));
    const expectedVersion = document.version;

    const listener = vscode.workspace.onDidChangeTextDocument((event) => {
        if (event.document !== document || event.document.version === expectedVersion) {
            return;
        }

        listener.dispose();
        clearTimeout(giveUp);

        // After VS Code has finished its own caret and scroll handling for the edit.
        setTimeout(() => {
            if (vscode.window.activeTextEditor !== editor) {
                return;
            }

            editor.selections = targets.map((target) => new vscode.Selection(
                document.positionAt(target.anchor),
                document.positionAt(target.active),
            ));
            editor.revealRange(editor.selection, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
        }, 0);
    });

    // A format that never lands (cancelled, or refused by VS Code) must not leave the listener
    // waiting to move the caret on some later, unrelated edit.
    const giveUp = setTimeout(() => listener.dispose(), 5000);
}

/** The middleware hooks: the edits pass through unchanged, and the caret is restored after. */
export const caretRestoreMiddleware: Middleware = {
    async provideDocumentFormattingEdits(document, options, token, next) {
        const edits = await next(document, options, token);
        restoreCaretsAfter(document, edits ?? []);
        return edits;
    },
    async provideDocumentRangeFormattingEdits(document, range, options, token, next) {
        const edits = await next(document, range, options, token);
        restoreCaretsAfter(document, edits ?? []);
        return edits;
    },
};
