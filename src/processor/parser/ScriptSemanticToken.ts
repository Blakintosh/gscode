
/**
 * Class that stores a semantic token to be pushed to VSCode's API later
 * in processing.
 */
export class ScriptSemanticToken {
    readonly location: [number, number];
    readonly tokenType: string;
    readonly tokenModifiers?: string[];

    constructor(location: [number, number], tokenType: string, tokenModifiers: string[] | undefined = undefined) {
        this.location = location;
        this.tokenType = tokenType;
        this.tokenModifiers = tokenModifiers;
    }
}