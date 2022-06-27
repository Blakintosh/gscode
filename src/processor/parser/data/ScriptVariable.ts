import { ScriptValue } from "./ScriptValue";

export class ScriptVariable {
	name: string;
	constant: boolean;
	// TODO at later stage: add type-based parsing
	type: any;
	// TODO
	//value: ScriptValue;

	constructor(name: string, constant: boolean = false) {
		this.name = name;
		this.constant = constant;
	}
}