# W3C SCXML 1.0 conformance

**Date:** 2026-09-03 · **Suite:** `tests/XState.Scxml.Tests/W3cConformanceTests.cs` · **Corpus:** `tests/XState.Scxml.Tests/w3c/` (vendored, unmodified)

## Totals

| | Count | |
|---|---:|---|
| `test*.scxml` files in the corpus | 206 | |
| — invoked sub-documents (`test*sub1.scxml`) | 5 | not tests; loaded by `test216`, `test226`, `test239`, `test242`, `test276` |
| **IRP tests** | **201** | |
| Excluded (manual) | 5 | `ExcludedManual` — no `#pass` state; the verdict needs a human reading the log |
| **Executed** | **196** | |
| **Passed** | **180** | 91.8% of executed, 89.6% of all IRP tests |
| Known failing | 16 | `KnownFailing` — every entry has a recorded reason |

`KnownFailing` is an *exact* list, not a floor: a test that is not listed must pass, and a test that
is listed must fail. An unexpected pass fails the suite with "remove from KnownFailing", so the list
cannot quietly rot as the implementation improves.

## Method

Each IRP test is a self-checking machine that ends in a top-level `<final id="pass"/>` or
`<final id="fail"/>`, so the harness only has to run the document and look at where it stopped.

`ScxmlConverter.Parse` compiles the document into an ordinary `StateMachine<ScxmlDataModel>` — the
state tree, transition selection (including `type="internal"`, which becomes the core's explicit
transition domain), initial and history *default* transitions with their executable content,
donedata and invocations — `autoforward` and `<finalize>` included — are all expressed with pure
core primitives, and the SCXML-specific parts (expressions, executable content) become actions that
run against a [Jint](https://github.com/sebastienros/jint) engine holding the `ecmascript`
datamodel. The machine is then driven by `DeterministicInterpreter`, a single-threaded virtual-time
driver: no threads, no wall clock, delayed `<send>`s land in a set keyed by (virtual time,
sequence) and are released in order, so every run is reproducible and the whole 196-test suite
finishes in about 200 ms. Each document is run twice: once as written, and once after a
`Parse → ToScxml → Parse` round trip, which must reach the same verdict (`SurvivesAnScxmlRoundTrip`).
That is the only end-to-end check on the serializer — a structural comparison would miss the parts
that actually decide behaviour, such as executable content recovered from source elements, the
`event`/`cond` text of transitions whose matching lives in a guard, the content of `<initial>` and
history default transitions, `binding`, state-local `<datamodel>` and `<donedata>`. Each test gets a 120 s *virtual* time budget plus the interpreter's
runaway guard (100 000 delivered events); a timeout, an exception, an `Error` status, or reaching
`#fail` all count as a failure. After every step the harness drains
`ScxmlDataModel.PendingInternalEvents` — the documented channel for platform errors produced by
pure contexts, such as a transition `cond` that throws — and feeds them back as internal events.
Invoked children are ordinary machine logic and run as child actors of the same interpreter, with
`#_parent` and `#_<invokeid>` resolved as actor addresses. `autoforward` and `<finalize>` are the
core's per-invocation pre-selection hooks (`InvokeDefinition.AutoForward` /
`OnExternalEvent`), which run for every external event before transitions are selected;
`<finalize>` recognizes the events of *its own* invocation by `_event.invokeid`, which the driver
stamps on child-to-parent events (`DecorateChildEvent`). `#_scxml_<sessionid>` is resolved by the
`<send>` itself: an unknown session raises `error.communication` inline, in document order, ahead of
anything raised later in the same block, which a runtime that only discovers the failure at delivery
time cannot do.

## Known failing, by bucket

Every remaining failure is an optional or unimplementable feature: the BasicHTTP I/O processor, an
XML DOM in the datamodel, or one document this harness cannot score. No SCXML *semantics* are on
the list.

### Basic HTTP Event I/O Processor — 12 tests

Optional in the IRP. Only the SCXML Event I/O Processor is implemented; any other `<send type>` is
rejected with `error.execution`.

| Test | Reason |
|---|---|
| test201 | Requires the Basic HTTP Event I/O Processor. |
| test509 | POST delivery to an accessURI. |
| test510 | Inbound HTTP messages must land in the external queue. |
| test518 | `namelist` values encoded as POST parameters. |
| test519 | `<param>` values encoded as POST parameters. |
| test520 | `<content>` sent as the message body. |
| test522 | `_ioprocessors['basichttp'].location` used as a send target. |
| test531 | The `_scxmleventname` parameter names the generated event. |
| test532 | The HTTP method names the event when `_scxmleventname` is absent. |
| test534 | `<send event>` transmitted as the `_scxmleventname` parameter. |
| test567 | Message content other than `_scxmleventname` populates `_event.data`. |
| test577 | A targetless basichttp `<send>` must raise `error.communication`; SCXML §6.2.4 makes an *unsupported* `<send type>` an `error.execution`, which is what happens here instead. |

### XML values in the ECMAScript datamodel — 3 tests

SCXML §B.2 requires an XML `<data>`/`<content>` body to become a DOM node. Jint has no DOM, so such
bodies fall back to JSON parsing and then to a space-normalized string.

| Test | Reason |
|---|---|
| test530 | `<invoke><content expr>` resolving to an SCXML document held in a variable. |
| test557 | An XML `<data>` body/`src` must expose `getElementsByTagName`. |
| test561 | XML `<send><content>` must arrive as a DOM node in `_event.data`. |

### Harness convention — 1 test

| Test | Reason |
|---|---|
| test301 | The IRP expects the document to be *rejected*, and it is: `<script src>` is fetched through the same resolver `<data src>` uses, and a document whose script cannot be fetched throws out of `ScxmlConverter.Parse`. There is no `#pass` state to reach, so the harness can only score that rejection as a failure. |

## Excluded (manual) — 5 tests

Not executed. None of these documents has a `#pass` state, and in each the property under test is
only visible in the log, so the IRP asks a human to read it. (test178 and test230 do contain a
reachable `<final id="fail"/>`, but it only catches gross failures — reaching the success state says
nothing about the property being tested.)

| Test | Why it is manual |
|---|---|
| test178 | Success is `<final id="final">`; the point of the test — that a `<send>` carries *both* same-named `<param>` pairs — is only observable in the logged `_event.raw`, whose format the SCXML I/O processor deliberately leaves unspecified. |
| test230 | Success is `<final id="final">`; the verdict is whether the parent's and the child's logged event fields match for an autoforwarded event. `autoforward` itself is implemented and covered by test229. |
| test250 | The tester must read the cancelled child's log; the parent cannot observe the child after cancellation. |
| test307 | The tester must compare logged values for `binding="late"` access before and after declaration. The document reaches `<final id="final">` whichever way it goes, so there is no verdict to score; `ScxmlParserTests.LateBoundAccessBeforeDeclarationBehavesLikeAMissingSubstructure` makes the comparison instead — both reads yield `undefined` and neither reports an error. |
| test415 | The tester must confirm an event raised in a top-level final state is never processed. |
