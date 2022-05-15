/**
 * Class that stores built-in script functions for use with VSCode Autofill.
 * Can be used for either GSC or CSC. These two should be kept separate ideally
 */
export class BuiltInScriptFunction {
    name: String;
    description: String;
    example: String;
    fill: String;
    auto: boolean;

    /**
     * Creates a definition for a built-in script function based on the Treyarch docs
     * @param name Function name for our autocomplete, etc
     * @param description Description for function
     * @param example An example of the function in use
     * @param fill What the editor should fill with if this function is entered
     * @param auto Whether this has been manually verified to be correct or not (since Treyarch's docs are so flaky)
     */
    constructor(name: String, description: String, example: String, fill: String, auto: boolean = true) {
        this.name = name;
        this.description = description;
        this.example = example;
        this.fill = fill;
        this.auto = auto;
    }
}