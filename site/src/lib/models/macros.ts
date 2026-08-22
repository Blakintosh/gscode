import { z } from 'zod';

/**
 * The macro library — the stock preprocessor macros documented from the mod tools' `.gsh`
 * headers, plus the compiler's built-ins. Mirrors `data/macros/SCHEMA.md`, which is the format's
 * source of truth; `append_macro.py` enforces it on the artifact, so this schema can stay strict
 * where `library.ts` has to tolerate five generations of function artifacts.
 */

export const GshMacroKinds = ['constant', 'function', 'builtin'] as const;
export type GshMacroKind = (typeof GshMacroKinds)[number];

export const GshMacroParameterSchema = z.object({
	name: z.string(),
	description: z.string()
});
export type GshMacroParameter = z.infer<typeof GshMacroParameterSchema>;

/** One stock `#define` of the name. A macro defined by both an mp and a zm header has two. */
export const GshMacroDefinitionSchema = z.object({
	/** Forward-slash path relative to the mod tools root, e.g. `scripts/shared/shared.gsh`. */
	path: z.string(),
	/** 1-based line of the `#define`. */
	line: z.number(),
	/** Declaration-order parameters for function-like macros; null on object-like ones. */
	parameters: z.array(GshMacroParameterSchema).nullish(),
	/** The body as defined, whitespace collapsed, trailing comment stripped. May be empty. */
	expansion: z.string()
});
export type GshMacroDefinition = z.infer<typeof GshMacroDefinitionSchema>;

export const GshMacroSchema = z.object({
	name: z.string(),
	kind: z.enum(GshMacroKinds),
	description: z.string(),
	/** Empty exactly when `kind` is `builtin` — the compiler substitutes those itself. */
	definitions: z.array(GshMacroDefinitionSchema),
	example: z.string().nullish(),
	remarks: z.string().nullish(),
	flags: z
		.array(z.string())
		.nullish()
		.transform((arg) => arg ?? []),
	confidence: z.enum(['low', 'medium', 'high']).nullish()
});
export type GshMacro = z.infer<typeof GshMacroSchema>;

export const GshLibrarySchema = z.object({
	macros: z.array(GshMacroSchema),
	gameId: z.string(),
	languageId: z.string(),
	revisedOn: z
		.string()
		.nullish()
		.transform((arg) => (arg ? new Date(arg) : null)),
	revision: z
		.number()
		.nullish()
		.transform((arg) => arg ?? 0)
});
export type GshLibrary = z.infer<typeof GshLibrarySchema>;
