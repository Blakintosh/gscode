import { BuiltInScriptFunction } from "./builtInScriptFunction";

export abstract class ScriptProvider {
    builtins: Set<BuiltInScriptFunction>;

    constructor() {
        this.builtins = new Set<BuiltInScriptFunction>();
    }

    // TODO: Subject to change
    /**
     * Gets the suggested autofills for this term
     * @param term The term being typed
     */
    abstract getSuggestions(term: String): Array<BuiltInScriptFunction>;
}