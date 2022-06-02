import * as vscode from 'vscode';

export class ScriptProcessor {
	static semanticProvider(document: vscode.TextDocument, builder: vscode.SemanticTokensBuilder): void {
		// Resolving end line/position
		/*const lines = document.getText().split(/\r\n|\r|\n/);
		// Get end line and end position
		const endLine = lines.length - 1;
		const endPosition = lines[endLine].length;*/

		/*let range = document.getWordRangeAtPosition(new vscode.Position(0, 0));

		while(range !== undefined && range.end !== undefined) {
			const word = document.getText(range);

			console.log(word);

			range = document.getWordRangeAtPosition(new vscode.Position(range.end.line, range.end.character + 1));
		}*/

		let text = document.getText();

		const fileLength = text.length;
		const lines = text.split(/\r\n|\r|\n/);
		let lineLengths = new Array<number>();
		for(let i = 0; i < lines.length; i++) {
			lineLengths[i] = lines[i].length;
		}

		//let test = new FunctionCall();
		//test.tokenMatches(0);

		console.log("parsing file of length "+fileLength);

		let textIndex = 0;
		let currentLineBaseIndex = 0;

		while(textIndex < fileLength) {
			console.log("base index is at "+textIndex);
			let nextToken = text.search(/\S/);

			if(nextToken === -1) { break; };

			text = text.substring(nextToken);

			// Next token has been found, analyse it
			// Document Root expects preprocessor related items, class declarations and function declarations.
			
			console.log(text);

			textIndex += nextToken;

			nextToken = text.search(/\s/);
			text = text.substring(nextToken);

			if(nextToken === -1) { break; };

			textIndex += nextToken;
		}

		/*builder.push(
			new vscode.Range(new vscode.Position(1, 0), new vscode.Position(1, 5)),
			'parameter',
			['declaration']
			);*/
	}

	static analyseToken() {

	}
}