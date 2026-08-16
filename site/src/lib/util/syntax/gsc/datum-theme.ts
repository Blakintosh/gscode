/**
 * Datum syntax themes for Shiki. One hue does the identity; value does the range:
 * keywords sit in teal, calls in bright, literals in the action gradient's cool end,
 * comments in mute. Backgrounds are transparent — the code panel supplies the surface.
 * Violet is never text, so it does not appear here.
 */
import type { ThemeRegistrationRaw } from 'shiki';

function theme(
	name: string,
	type: 'dark' | 'light',
	c: {
		fg: string;
		keyword: string;
		call: string;
		literal: string;
		string: string;
		comment: string;
		type: string;
		punct: string;
		directive: string;
	}
): ThemeRegistrationRaw {
	return {
		name,
		type,
		colors: { 'editor.background': '#00000000', 'editor.foreground': c.fg },
		settings: [
			{ settings: { foreground: c.fg, background: '#00000000' } },
			{ scope: ['comment', 'punctuation.definition.comment'], settings: { foreground: c.comment } },
			{
				scope: ['comment.block.documentation.descriptor', 'entity.name.tag.documentation'],
				settings: { foreground: c.comment, fontStyle: 'bold' }
			},
			{
				scope: ['keyword.control', 'storage.type', 'storage.modifier', 'keyword.operator.function-pointer'],
				settings: { foreground: c.keyword }
			},
			{
				scope: ['keyword.control.directive', 'meta.preprocessor', 'entity.name.function.preprocessor'],
				settings: { foreground: c.directive }
			},
			{ scope: ['keyword.operator'], settings: { foreground: c.punct } },
			{
				scope: ['entity.name.function', 'meta.function-call entity.name.function'],
				settings: { foreground: c.call }
			},
			{
				scope: ['entity.name.namespace', 'entity.name.scope-resolution', 'entity.name.class', 'entity.other.inherited-class', 'entity.name.type'],
				settings: { foreground: c.type }
			},
			{ scope: ['constant.numeric', 'constant.language'], settings: { foreground: c.literal } },
			{ scope: ['string', 'punctuation.definition.string'], settings: { foreground: c.string } },
			{ scope: ['constant.character.escape'], settings: { foreground: c.keyword } },
			{ scope: ['punctuation'], settings: { foreground: c.punct } }
		]
	};
}

export const datumDark = theme('datum-dark', 'dark', {
	fg: '#E2EAEC',
	keyword: '#3ED1BD',
	call: '#7BEFDD',
	literal: '#5FC6E0',
	string: '#9AD9E8',
	comment: '#5A6C76',
	type: '#B8C6CB',
	punct: '#8B9BA3',
	directive: '#16A899'
});

export const datumLight = theme('datum-light', 'light', {
	fg: '#10181D',
	keyword: '#0F9484',
	call: '#0B6B60',
	literal: '#1F7F99',
	string: '#2A6F86',
	comment: '#5A6C76',
	type: '#3E5661',
	punct: '#5A6C76',
	directive: '#0B6B60'
});
