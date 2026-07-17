# GSCode.Parser

The pure per-file analysis pipeline: lexer → preprocessor → parser → extraction.
A deterministic function library — no I/O except through injected providers, and no
LSP types anywhere.

*(Empty at P0 — the lexer lands in P1, preprocessor in P2, parser in P3, extraction in P4.)*
