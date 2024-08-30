lexer grammar ScrLexer;

/// Comments
MULTILINECOMMENT    : '/*' .*? '*/'             -> channel(HIDDEN);
SINGLELINECOMMENT   : '//' ~[\r\n\u2028\u2029]* -> channel(HIDDEN);
DOCCOMMENT          : '/@' .*? '@/'             -> channel(HIDDEN);

/// Whitespace
WHITESPACES: [\t\u000B\u000C\u0020\u00A0]+ -> channel(HIDDEN);
LINETERMINATOR: [\r\n\u2028\u2029] -> channel(HIDDEN);

/// Punctuation
OPENPAREN           : '(';
CLOSEPAREN          : ')';
OPENBRACKET         : '[';
CLOSEBRACKET        : ']';
OPENBRACE           : '{';
CLOSEBRACE          : '}';
OPENDEVBLOCK        : '/#';
CLOSEDEVBLOCK       : '#/';

/// Operators
ASSIGNMENTBITWISELEFTSHIFT  : '<<=';
ASSIGNMENTBITWISERIGHTSHIFT : '>>=';
NOTTYPEEQUALS               : '!==';
TYPEEQUALS                  : '===';
SCOPERESOLUTION             : '::';
AND                         : '&&';
ASSIGNMENTBITWISEAND        : '&=';
ASSIGNMENTBITWISEOR         : '|=';
ASSIGNMENTBITWISEXOR        : '^=';
ASSIGNMENTDIVIDE            : '/=';
ASSIGNMENTMINUS             : '-=';
ASSIGNMENTREMAINDER         : '%=';
ASSIGNMENTMULTIPLY          : '*=';
ASSIGNMENTPLUS              : '+=';
BITLEFTSHIFT                : '<<';
BITRIGHTSHIFT               : '>>';
DECREMENT                   : '--';
EQUALS                      : '==';
GREATERTHANEQUALS           : '>=';
INCREMENT                   : '++';
LESSTHANEQUALS              : '<=';
NOTEQUALS                   : '!=';
OR                          : '||';
METHODACCESS                : '->';
ASSIGNMENT                  : '=';
AMPERSAND                   : '&';
BITWISENOT                  : '~';
BITWISEOR                   : '|';
DIVIDE                      : '/';
GREATERTHAN                 : '>';
LESSTHAN                    : '<';
MINUS                       : '-';
REMAINDER                   : '%';
MULTIPLY                    : '*';
NOT                         : '!';
PLUS                        : '+';
XOR                         : '^';
TERNARYSTART                : '?';
COLON                       : ':';
MEMBERACCESS                : '.';

/// Keywords
CLASS               : 'class';
FUNCTION            : 'function';
VAR                 : 'var';
RETURN              : 'return';
WAIT                : 'wait';
THREAD              : 'thread';
SELF                : 'self';
WORLD               : 'world';
CLASSES             : 'classes';
LEVEL               : 'level';
GAME                : 'game';
ANIM                : 'anim';
IF                  : 'if';
ELSE                : 'else';
DO                  : 'do';
WHILE               : 'while';
FOR                 : 'for';
FOREACH             : 'foreach';
IN                  : 'in';
NEW                 : 'new';
WAITTILL            : 'waittill';
WAITTILLMATCH       : 'waittillmatch';
WAITTILLFRAMEEND    : 'waittillframeend';
SWITCH              : 'switch';
CASE                : 'case';
DEFAULT             : 'default';
BREAK               : 'break';
CONTINUE            : 'continue';
NOTIFY              : 'notify';
ENDON               : 'endon';
ASSERT              : 'assert';
ASSERTMSG           : 'assertmsg';
CONSTRUCTOR         : 'constructor';
DESTRUCTOR          : 'destructor';
AUTOEXEC            : 'autoexec';
PRIVATE             : 'private';
CONST               : 'const';
VARARG              : 'vararg';
VARARGDOTS          : '...';
USINGANIMTREE       : '#using_animtree';
ANIMTREE            : '#animtree';
USING               : '#using';
INSERT              : '#insert';
DEFINE              : '#define';
HASHIF              : '#if';
NAMESPACE           : '#namespace';
PRECACHE            : '#precache';
/// These are reserved, but less strictly keywords
ISDEFINED           : 'isdefined';
VECTORSCALE         : 'vectorscale';
GETTIME             : 'gettime';
WAITREALTIME        : 'waitrealtime';
PROFILESTART        : 'profilestart';
PROFILESTOP         : 'profilestop';

/// Keyword Literals
UNDEFINEDLITERAL    : 'undefined';
BOOLLITERAL         : 'false' | 'true';

/// Numeric Literals

DECIMALLITERAL:
    DECIMALINTEGERLITERAL '.' [0-9] [0-9_]* EXPONENTPART?
    | '.' [0-9] [0-9_]* EXPONENTPART?
    | DECIMALINTEGERLITERAL EXPONENTPART?
;
HEXINTEGERLITERAL    : '0' [xX] [0-9a-fA-F] HEXDIGIT*;

/// Identifier Names and Identifiers
IDENTIFIER: IDENTIFIERSTART IDENTIFIERPART*;

/// String Literals
STRINGLITERAL:
    ('"' DOUBLESTRINGCHARACTER* '"' | '\'' SINGLESTRINGCHARACTER* '\'') {this.ProcessStringLiteral();}
;

// Fragment rules

fragment DOUBLESTRINGCHARACTER: ~["\\\r\n] | '\\' ESCAPESEQUENCE | LINECONTINUATION;

fragment SINGLESTRINGCHARACTER: ~['\\\r\n] | '\\' ESCAPESEQUENCE | LINECONTINUATION;

fragment ESCAPESEQUENCE:
    CHARACTERESCAPESEQUENCE
    | '0' // no digit ahead! TODO
    | HEXESCAPESEQUENCE
    | UNICODEESCAPESEQUENCE
    | EXTENDEDUNICODEESCAPESEQUENCE
;

fragment CHARACTERESCAPESEQUENCE: SINGLEESCAPECHARACTER | NONESCAPECHARACTER;

fragment HEXESCAPESEQUENCE: 'x' HEXDIGIT HEXDIGIT;

fragment UNICODEESCAPESEQUENCE:
    'u' HEXDIGIT HEXDIGIT HEXDIGIT HEXDIGIT
    | 'u' '{' HEXDIGIT HEXDIGIT+ '}'
;

fragment EXTENDEDUNICODEESCAPESEQUENCE: 'u' '{' HEXDIGIT+ '}';

fragment SINGLEESCAPECHARACTER: ['"\\bfnrtv];

fragment NONESCAPECHARACTER: ~['"\\bfnrtv0-9xu\r\n];

fragment ESCAPECHARACTER: SINGLEESCAPECHARACTER | [0-9] | [xu];

fragment LINECONTINUATION: '\\' [\r\n\u2028\u2029]+;

fragment HEXDIGIT: [_0-9a-fA-F];

fragment DECIMALINTEGERLITERAL: '0' | [1-9] [0-9_]*;

fragment EXPONENTPART: [eE] [+-]? [0-9_]+;

fragment IDENTIFIERPART: IDENTIFIERSTART | [\p{Mn}] | [\p{Nd}] | [\p{Pc}] | '\u200C' | '\u200D';

fragment IDENTIFIERSTART: [\p{L}] | [$_] | '\\' UNICODEESCAPESEQUENCE;
