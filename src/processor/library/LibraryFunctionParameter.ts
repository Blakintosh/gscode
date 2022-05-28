/**
 * Class that stores the information associated with a built-in function parameter.
 */
export class LibraryFunctionParameter {
	readonly name: string;
	readonly description: string;
	readonly mandatory: boolean;

	constructor(name: string, description: string, mandatory=true) {
		this.name = name;
		this.description = description;
		this.mandatory = mandatory;
	}
}