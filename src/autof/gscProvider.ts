import { BuiltInScriptFunction } from "./builtInScriptFunction";
import { ScriptProvider } from "./scriptProvider";

export class GSCProvider extends ScriptProvider {
    constructor() {
        super();

        // Add GSC functions here
    }

    getSuggestions(term: String): BuiltInScriptFunction[] {
        throw new Error("Method not implemented.");
    }
}