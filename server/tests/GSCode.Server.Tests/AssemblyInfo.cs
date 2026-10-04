using Xunit;

// Test COLLECTIONS in this assembly run one at a time.
//
// GameProfile.Active is process-global, and this assembly now contains classes that WRITE it — the
// corpus sweeps, which select a game before analysing it, and SampleScriptTests, which does the same
// for each sample folder — alongside three hundred that read it without saying so. xUnit's unit of
// parallelism is the collection, so those two groups ran at the same time: 132 handler and formatter
// tests failed because a sample run had left the active dialect on CoD4.
//
// GameProfileCollection serializes the writers against each other and cannot do more than that; a
// collection only orders the classes that join it. The readers are every other class here, and they
// join nothing, because reading a global is invisible at the call site.
//
// This is the second half of the fix and not the whole of it. SampleWorkspace also puts Active back
// when it is done, which is what stopped the failures — serializing alone left 121 of them, since a
// class that runs AFTER a sample run reads the leftover dialect just as happily as one running
// beside it. The restore closes the window this closes the race on; neither is sufficient alone
// while a global decides what the parser and the lints do.
//
// Measured rather than assumed: 313 tests, 1s parallel against 2s serial. Within a test parallelism
// is untouched — the corpus sweeps still walk their files through Parallel.ForEachAsync, which is
// where this assembly's time actually goes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
