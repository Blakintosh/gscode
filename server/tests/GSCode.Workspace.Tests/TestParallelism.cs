using Xunit;

// One test class here switches the dialect — GlobalObjectWriteLintTests sets cod4 to prove that
// `world` is an ordinary name where the engine has no such global, then sets bo3 back. GameProfile
// .Active is PROCESS-GLOBAL, so while that window is open every other class running in parallel
// analyses under cod4: `class`, `const` and constructors stop existing and their tests fail, in a
// different combination on every run depending on what happened to overlap.
//
// GSCode.Server.Tests already answers this with GameProfileCollection, whose comment states the
// rule — reading Active while another class writes it is the same race from the other side. Here
// every class analyses, so serializing the collections IS that rule for this assembly rather than
// a weaker version of it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
