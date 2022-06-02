// \/\/.*

/* eslint-disable @typescript-eslint/naming-convention */
import { Token, TokenType } from "../Token";

/**
 * Comment types in GSC
 */
enum CommentTypes {
	Line = "//",
	Block = "/*",
	DocBlock = "/@"
}

/**
 * Comment GSC Token
 */
export class Comment extends Token {
	getType(): TokenType {
		return TokenType.Comment;
	}

	getSpecificType(): CommentTypes {
		return CommentTypes.Line;
	}

	getRegex(): RegExp {
		return /\/\/.*|\/\*(?:.|\r\n)*?\*\/|\/@(?:.|\r\n)*?@\//m;
	}
}