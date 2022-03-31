// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';

export function provide()
{
    const tokenTypes = ['class', 'interface', 'enum', 'function', 'variable'];
    const tokenModifiers = ['declaration', 'documentation'];
    const legend = new vscode.SemanticTokensLegend(tokenTypes, tokenModifiers);

    const provider: vscode.DocumentSemanticTokensProvider = {
    provideDocumentSemanticTokens(
        document: vscode.TextDocument
    ): vscode.ProviderResult<vscode.SemanticTokens> {
        // analyze the document and return semantic tokens

        const tokensBuilder = new vscode.SemanticTokensBuilder(legend);
        // on line 1, characters 1-5 are a class declaration
        tokensBuilder.push(
        new vscode.Range(new vscode.Position(1, 1), new vscode.Position(1, 5)),
        'class',
        ['declaration']
        );
        return tokensBuilder.build();
    }
    };

    const selector = { language: 'java', scheme: 'file' }; // register for all Java documents from the local file system

    vscode.languages.registerDocumentSemanticTokensProvider(selector, provider, legend);
}