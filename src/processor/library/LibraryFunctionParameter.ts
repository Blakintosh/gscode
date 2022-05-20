export class LibraryFunctionParameter {
	readonly name: String;
	readonly description: String;
	readonly mandatory: boolean;

	constructor(name: String, description: String, mandatory=true) {
		this.name = name;
		this.description = description;
		this.mandatory = mandatory;
	}
}