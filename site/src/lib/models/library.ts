import { z } from 'zod';

// Base schema without union (used for lazy reference)
const ScrDataTypeBaseSchema = z.object({
	dataType: z.string(),
	instanceType: z.string().nullish(),
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
	type: ScrDataTypeSchema.nullish()
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
	flags: z.array(z.string()),
	example: z.string().nullish(),
	verifiedInRevision: z.number().nullish(),
	remarks: z.array(z.string()).nullish()
});
export type ScrFunction = z.infer<typeof ScrFunctionSchema>;

export const ScrLibrarySchema = z.object({
	api: z.array(ScrFunctionSchema),
	gameId: z.string(),
	languageId: z.string(),
	revisedOn: z.string().transform((arg) => new Date(arg)),
	revision: z.number()
});
export type ScrLibrary = z.infer<typeof ScrLibrarySchema>;
