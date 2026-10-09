/** The Mod Tools page: Apex, Blackbird and Ultrasound. Keep versions in step with each app's releases. */

const github = (repo: string) => `https://github.com/Blakintosh/${repo}`;

export type ToolId = 'apex' | 'blackbird' | 'ultrasound';

export interface Tool {
	id: ToolId;
	name: string;
	version: string;
	/** Shown on the version chip; a preview says so everywhere its version does. */
	status: 'Preview' | 'Release';
	repoUrl: string;
	releasesUrl: string;
	issuesUrl: string;
	mascot: string;
	/** What it does, in one line. */
	job: string;
	/** The stock file it takes the place of. */
	replaces: string;
	needs: string;
	licence: string;
	/** Tailwind classes for its colour: the marker fill and the colour as text. */
	mark: string;
	ink: string;
}

const tool = (t: Omit<Tool, 'repoUrl' | 'releasesUrl' | 'issuesUrl'> & { repo: string }): Tool => {
	const { repo, ...rest } = t;
	return {
		...rest,
		repoUrl: github(repo),
		releasesUrl: `${github(repo)}/releases`,
		issuesUrl: `${github(repo)}/issues`
	};
};

export const tools: Record<ToolId, Tool> = {
	apex: tool({
		id: 'apex',
		name: 'Apex',
		version: '0.2',
		status: 'Preview',
		repo: 'apex',
		mascot: '/images/tools/apex.webp',
		job: 'Edits GDTs: weapons, models, materials, tables.',
		replaces: 'APE · asseteditor_modtools.exe',
		needs: 'Nothing extra. The release carries its own runtime.',
		licence: 'GPLv3',
		mark: 'bg-eye',
		ink: 'text-eye-ink'
	}),
	blackbird: tool({
		id: 'blackbird',
		name: 'Blackbird',
		version: '0.9',
		status: 'Preview',
		repo: 'blackbird',
		mascot: '/images/tools/blackbird.webp',
		job: 'Builds, runs and publishes maps and mods.',
		replaces: 'Launcher · modlauncher.exe',
		needs: ".NET 10 runtime. The installer tells you if it's missing.",
		licence: 'MIT',
		mark: 'bg-burn',
		ink: 'text-burn-ink'
	}),
	ultrasound: tool({
		id: 'ultrasound',
		name: 'Ultrasound',
		version: '1.0',
		status: 'Release',
		repo: 'ultrasound',
		mascot: '/images/tools/ultrasound.webp',
		job: "Converts a map's sounds into banks during the build.",
		replaces: 'snd_convert.exe',
		needs: 'Nothing extra',
		licence: 'MIT',
		mark: 'bg-sonar',
		ink: 'text-sonar-ink'
	})
};

export const toolList = [tools.apex, tools.blackbird, tools.ultrasound];

/** "Preview v0.2" or "v1.0". */
export const versionChip = (t: Tool) =>
	(t.status === 'Preview' ? 'Preview ' : '') + 'v' + t.version;
