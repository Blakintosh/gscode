export class ScriptDependency {
	readonly file: string;
	readonly location: [number, number];
	uses: number = 0;

	constructor(file: string, location: [number, number]) {
		this.file = file;
		this.location = location;
	}
}