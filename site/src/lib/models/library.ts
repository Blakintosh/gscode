import { z } from 'zod';

// Known entity types
export const ScrEntityTypes = [
	'weapon',
	'vehicle',
	'player',
	'actor',
	'aitype',
	'pathnode',
	'sentient',
	'vehiclenode',
	'hudelem'
] as const;

export type ScrEntityType = (typeof ScrEntityTypes)[number];

// Base schema without union (used for lazy reference)
const ScrDataTypeBaseSchema = z.object({
	dataType: z.string(),
	// Legacy field - preserved for backwards compatibility, will be removed later
	instanceType: z.string().nullish(),
	// New unified field for entity/enum/class sub-types (prioritized over instanceType)
	subType: z.string().nullish(),
	isArray: z.boolean().nullish()
});

// Full schema with optional union support
export const ScrDataTypeSchema: z.ZodType<ScrDataType> = ScrDataTypeBaseSchema.extend({
	// For union types - if present, this type is a union of these types
	unionOf: z.lazy(() => z.array(ScrDataTypeSchema)).nullish()
});

export type ScrDataType = z.infer<typeof ScrDataTypeBaseSchema> & {
	unionOf?: ScrDataType[] | null;
};

export const ScrFunctionParameterSchema = z.object({
	name: z.string().nullish(),
	description: z.string().nullish(),
	mandatory: z.boolean().nullish(),
	type: ScrDataTypeSchema.nullish(),
	variadic: z.boolean().nullish()
});
export type ScrFunctionParameter = z.infer<typeof ScrFunctionParameterSchema>;

export const ScrReturnValueSchema = ScrFunctionParameterSchema.omit({
	mandatory: true
}).extend({
	void: z.boolean().nullish()
});
export type ScrReturnValue = z.infer<typeof ScrReturnValueSchema>;

export const ScrFunctionOverloadSchema = z.object({
	calledOn: ScrFunctionParameterSchema.nullish(),
	parameters: z.array(ScrFunctionParameterSchema),
	returns: ScrReturnValueSchema.nullish()
});
export type ScrFunctionOverload = z.infer<typeof ScrFunctionOverloadSchema>;

export const ScrFunctionSchema = z.object({
	name: z.string(),
	description: z.string().nullish().default('No description.'),
	overloads: z.array(ScrFunctionOverloadSchema),
	// Absent entirely on some pre-BO3 entries — 113 of World at War's, where the wordfile gave a name
	// and nothing else to say about it. Resolved to an empty array rather than left nullable, since
	// every reader treats this as a list and asks it `.includes(...)`.
	flags: z
		.array(z.string())
		.nullish()
		.transform((arg) => arg ?? []),
	example: z.string().nullish(),
	verifiedInRevision: z.number().nullish(),
	remarks: z.array(z.string()).nullish(),
	confidence: z.enum(['low', 'medium', 'high']).nullish(),
	// The pre-BO3 libraries carry two fields BO3's do not. They are modelled here because `z.object`
	// STRIPS what it does not know: without these, loading CoD4 into the editor and exporting it
	// would silently delete its 38 categories and every SP/MP marking.
	/** The engine category a function belongs to (Math, AI, Player, …). Pre-BO3 libraries only. */
	module: z.string().nullish(),
	/** Which game modes the function exists in — `SP`, `MP`. Pre-BO3 libraries only. */
	spmp: z.string().nullish(),
	/** Whether the function is only available inside a dev block. */
	devOnly: z.boolean().nullish()
});
export type ScrFunction = z.infer<typeof ScrFunctionSchema>;

/**
 * How much a library may be trusted, carried alongside it so a page does not have to know which
 * game it is looking at. Stamped by `/api/getLibrary` from the game registry, which mirrors the
 * language server's `GameProfile`.
 */
export const ScrProvenanceSchema = z.object({
	/** What the entries were built from. */
	source: z.enum(['documentation', 'wordfile', 'reconstructed']),
	/** Whether the function list is exhaustive — `HasCompleteBuiltinLibrary`. */
	complete: z.boolean(),
	/** Whether the parameters may be judged against — `HasReliableBuiltinSignatures`. */
	reliableSignatures: z.boolean(),
	/** The sibling game whose entries fill this one in, where they are borrowed. */
	inheritsFrom: z.string().nullish()
});
export type ScrProvenance = z.infer<typeof ScrProvenanceSchema>;

export const ScrLibrarySchema = z.object({
	api: z.array(ScrFunctionSchema),
	gameId: z.string(),
	languageId: z.string(),
	// Nullable because only Black Ops III's artifacts carry the envelope; the four pre-BO3 files are
	// a bare `{ api: [...] }`, and the endpoint completes what it can rather than inventing a date.
	revisedOn: z
		.string()
		.nullish()
		.transform((arg) => (arg ? new Date(arg) : null)),
	// Absent on the pre-BO3 artifacts. Resolved to 0 here rather than left nullable, so a revision is
	// always a number to compare and increment — only `revisedOn` has no honest stand-in.
	revision: z
		.number()
		.nullish()
		.transform((arg) => arg ?? 0),
	provenance: ScrProvenanceSchema.nullish()
});
export type ScrLibrary = z.infer<typeof ScrLibrarySchema>;
