---
name: verify-before-fixing
description: How to confirm a bug in this repo before changing code, and how to prove the fix. Use when starting from a report, a stack trace, or a hypothesis — the practice below caught wrong diagnoses repeatedly, including cases where the "fix" was not needed at all.
---

# Verify, then fix

## Reproduce in a test FIRST, against the unfixed code

Not after. A test written once the fix is in proves the code passes its own test, which is a
weaker claim than it looks.

The procedure, when a fix is already drafted:

```bash
cp <file> $SCRATCH/file.fixed.cs      # save the fix
git checkout <file>                    # back to broken
# write the test, run it, watch it FAIL for the reported reason
cp $SCRATCH/file.fixed.cs <file>       # restore
# run again, watch it pass
```

This has changed the answer more than once here:

- A parser infinite loop: the reproduction taken from the user's screenshot **passed** on the
  broken parser. The real trigger was a different shape entirely, and only the failing test found
  it. Writing the test afterwards would have shipped a test that proved nothing.
- A proposed guard on two more parser loops: 32 cases across every loop said **no fix was needed**.
  The loops shared a shape but not the defect — their recovery helpers advance, and the failing
  one's does not.

## A hang is often a memory bug

A non-terminating loop that appends a diagnostic each pass presents as unbounded memory, not as a
freeze. If a report mentions a memory leak in the editor, suspect a loop that stopped making
progress on half-typed text.

Guard loops with a position check rather than auditing every path:

```csharp
int before = _index;
statements.Add(ParseStatement());
if ( _index == before ) { Advance(); }
```

## Trust the stack over the report

A user's account of *when* something happened is evidence; their account of *what* is a hypothesis.
The semantic-token desync was reported as a highlighting bug and was three defects, one of which
was a stale-analysis problem in a completely different handler. The parser hang was reported
against a semantic-token change that only *amplified* a pre-existing bug.

Read the stack frame by frame before deciding what changed.

## Grep before explaining

Twice, a plausible explanation was written into a code comment and was wrong:

- ALL_CAPS names flagged by a lint were "obviously" macros from an unresolved header. One `grep`
  showed `BRIDGE_COLLAPSE_SPEED = 1.0;` — a file-scope constant, and the dialect has no `#define`.
- A missing builtin was "obviously" absent from the API. It was present, in the GSC library, and
  the call site was `.csc`.

If a comment asserts why something is, confirm it. A wrong explanation in a comment outlives the
wrong code around it.

## Measure a rule before choosing its severity

Anything that reports on user code gets swept over the corpus first — ~7,300 shipped scripts across
five games. Read the TOP REPORTED NAMES, not just the count: a shared shape among them is a
language fact the rule has not learned, not a defect rate in code that ships and works.

Assert a RATE rather than a count, so the test stays meaningful as corpora change.

## Check the run actually ran

A `Category=Corpus` pass over five games takes two to three minutes. Finishing in milliseconds
means the environment variables were not visible and every test no-opped — proving nothing while
looking exactly like success.
