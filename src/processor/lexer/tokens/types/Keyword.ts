/**
	GSCode Language Extension for Visual Studio Code
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Special Tokens in GSC. Ordered by (loosely) char count as first match will be used
 */
// TODO: self, world, level, game removed from keywords, as they are used as vars, but need to be globally applied
export enum KeywordTypes {
	Classes = "classes",
	Function = "function",
	Var = "var",
	Return = "return",
	Thread = "thread",
	Undefined = "undefined",
	Class = "class",
	Anim = "anim",
	If = "if",
	Else = "else",
	Do = "do",
	While = "while",
	Foreach = "foreach",
	For = "for",
	In = "in",
	New = "new",
	WaittillFrameEnd = "waittillframeend",
	WaittillMatch = "waittillmatch",
	WaitRealTime = "waitrealtime",
	Wait = "wait",
	Switch = "switch",
	Case = "case",
	Default = "default",
	Break = "break",
	Continue = "continue",
	False = "false",
	True = "true",
	AssertMsg = "assertmsg",
	Assert = "assert",
	Constructor = "constructor",
	Destructor = "destructor",
	Autoexec = "autoexec",
	Private = "private",
	Const = "const",
	VectorScale = "vectorscale",
	GetTime = "gettime",
	ProfileStart = "profilestart",
	ProfileStop = "profilestop",
	UsingAnimTree = "#using_animtree",
	Animtree = "#animtree",
	Using = "#using",
	Insert = "#insert",
	Namespace = "#namespace",
	Precache = "#precache",
	Size = "size"
}

/**
 * Special GSC Token
 * Accessing methods, comments, etc.
 * According to Treyarch spec., except () and [] have been omitted as they are in Punctuation
 */
export class Keyword extends Token {
	type: string = KeywordTypes.Classes;

	populate(contents: string, start: number, end: number): void {
		super.populate(contents, start, end);

		for(const keyword of Object.values(KeywordTypes)) {
			if(keyword === contents) {
				this.type = keyword;
				break;
			}
		}
	}

	getType(): TokenType {
		return TokenType.Keyword;
	}

	getSpecificType(): string {
		return this.type;
	}

	getRegex(): RegExp {
		return /(classes|function|var|return|thread|undefined|class|anim|#if|#elif|#else|if|else|do|while|foreach|for|in|new|waittillframeend|waittillmatch|waitrealtime|wait|switch|case|default|break|continue|false|true|assertmsg|assert|constructor|destructor|autoexec|private|const|vectorscale|gettime|profilestart|profilestop|#using_animtree|#animtree|#using|#insert|#namespace|#precache|#define|size)(?=\W)/;
	}
}