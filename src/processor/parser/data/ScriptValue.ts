/* eslint-disable @typescript-eslint/naming-convention */
export enum ScriptValueTypes {
	String,
	Integer,
	Float,
	Bool,
	Undefined,
	Unknown
}

export class ScriptValue {
	type: ScriptValueTypes;
	value: any;
	// Whether this is the true value of this variable, or whether we are using a placeholder value for a constant we can't determine
	unknownVar: boolean;

	constructor(type: ScriptValueTypes, value: any, unknownVar: boolean = false) {
		this.type = type;
		this.value = value;
		this.unknownVar = unknownVar;
	}
}