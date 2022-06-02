/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Special Tokens in GSC. Ordered by (loosely) char count as first match will be used
 */
enum KeywordTypes {
	Classes = "classes",
	Function = "function",
	Var = "var",
	Return = "return",
	Thread = "thread",
	Undefined = "undefined",
	Self = "self",
	World = "world",
	Class = "class",
	Level = "level",
	Game = "game",
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
	Waittill = "waittill",
	Wait = "wait",
	Switch = "switch",
	Case = "case",
	Default = "default",
	Break = "break",
	Continue = "continue",
	False = "false",
	True = "true",
	Notify = "notify",
	Endon = "endon",
	AssertMsg = "assertmsg",
	Assert = "assert",
	Constructor = "constructor",
	Destructor = "destructor",
	Autoexec = "autoexec",
	Private = "private",
	Const = "const",
	IsDefined = "isdefined",
	VectorScale = "vectorscale",
	GetTime = "gettime",
	ProfileStart = "profilestart",
	ProfileStop = "profilestop",
	UsingAnimTree = "#using_animtree",
	Animtree = "#animtree",
	Using = "#using",
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

		for(const keyword in KeywordTypes) {
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
		return /(classes|function|var|return|thread|undefined|self|world|class|level|game|anim|#if|#elif|#else|if|else|do|while|foreach|for|in|new|waittillframeend|waittillmatch|waitrealtime|waittill|wait|switch|case|default|break|continue|false|true|notify|endon|assertmsg|assert|constructor|destructor|autoexec|private|const|isdefined|vectorscale|gettime|profilestart|profilestop|#using_animtree|#animtree|#using|#namespace|#precache|#define|size)/;
	}
}