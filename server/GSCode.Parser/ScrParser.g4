parser grammar ScrParser;

options {
    tokenVocab=ScrLexer;
}

script
    : dependencies? statements? EOF
    ;

dependencies
    : usingStatement
    : insertStatement
    ;