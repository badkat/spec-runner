# Writing a methodology for Spec Runner

This document assumes you know nothing about this project. By the end you will understand how a
run works, and you will have built a complete methodology of your own.

You do not need to read `README.md` first. There is some overlap; this one goes deeper on the
parts you touch when authoring, and skips some operational detail the README covers.

**Contents**

1. [What the application is](#1-what-the-application-is)
2. [The five ideas you need](#2-the-five-ideas-you-need)
3. [The Step API](#3-the-step-api)
4. [Prompt templates](#4-prompt-templates)
5. [Worked example: release notes](#5-worked-example-release-notes)
6. [What lands on disk](#6-what-lands-on-disk)
7. [Rules the startup check enforces](#7-rules-the-startup-check-enforces)
8. [Testing a methodology without spending money](#8-testing-a-methodology-without-spending-money)
9. [Mistakes that are easy to make](#9-mistakes-that-are-easy-to-make)

---

## 1. What the application is

A Windows CLI that hosts a local web server and opens a browser console. It runs **one
methodology, hardcoded** — there is no manifest format, no DSL, no plugin registry. The
methodology is C# you write, compiled into the application.

A methodology is **phases containing tasks containing steps**. The step is the atomic unit.
Every step is deterministic C# — *including* the steps that call a language model.

The model is treated as a stateless text transformer reached over an OpenAI-compatible HTTP API.
It has no tools, no functions, and no say in what runs next. It produces text; code decides what
that text means and where the run goes. That constraint is not a style preference — it is
enforced at runtime in both directions, and if you try to work around it the application halts.

```
dotnet run --project src/SpecRunner
```

The terminal shows startup checks and then goes quiet. The browser shows the plan and waits for
you to press start.

---

## 2. The five ideas you need

### 2.1 Position is derived, not stored

There is no saved cursor and no serialized continuation. The workflow is a plain `foreach` over
your step list, executed from the beginning on every run. A step whose result is already
committed to disk short-circuits instead of re-executing.

**The filesystem is not a checkpoint of position — it is the position.**

Stopping is ending the process. Resuming is running it again. That is the whole model, and it is
why you will see no "resume" logic anywhere in your steps.

### 2.2 Skipping is earned, not assumed

Before a step is skipped, the completion record from its last run is checked: every input it
names must still hash to the same value. Inputs are named by **content hash**, never by
timestamp — file modification times are not consulted anywhere in this application.

If an input changed, the record is stale. It is invalidated, and so is everything reachable
downstream of it in the dependency graph.

This is why your declarations matter so much. They are not documentation.

### 2.3 Declarations are the graph

Each step declares, as static metadata beside its code, the variables and artifacts it reads and
writes. The dependency graph is built from those declarations at startup, **without executing
anything**, and validated before the web server binds.

A step physically cannot read something it did not declare: the state accessor throws on an
undeclared key rather than returning a value. So the graph is not a description of what your
steps probably do — it is the only thing they are able to do.

### 2.4 Code owns every branch

A step may read a value the model produced. A step may not ask the model what to do.

In practice this means: your validator prompt emits a `verdict:` field from a closed enum, and
your branching step is a `switch` over that enum with no `default` fallthrough. The prose beside
the verdict is for a human and is never read by code.

Every branch writes a **decision record** to disk naming the predicate, the exact value it
evaluated, the options, the branch taken, and the step that follows.

### 2.5 When it cannot decide, it stops

There is no auto-resolution of ambiguity. A step that needs a human writes a question file and
blocks. The answer is also a file. You answer in the browser, or you write the file in your
editor — both produce the same record, and the step cannot tell the difference.

Answers come from a closed enum **you define in code**. There is no free-text answer mode,
because consuming free text would mean interpreting it.

---

## 3. The Step API

Everything lives in `src/SpecRunner/Workflow/`. You will edit exactly two files:
`Methodology.cs` (the ordered list) and `MethodologySteps.cs` (the classes).

### 3.1 The base class

```csharp
public abstract class Step
{
    public abstract string Id { get; }               // "phase-1/task-2/step-3/name"
    public abstract string Description { get; }      // one line, shown in the plan

    public virtual IReadOnlyList<string> ReadsVariables  => [];
    public virtual IReadOnlyList<string> WritesVariables => [];
    public virtual IReadOnlyList<string> ReadsArtifacts  => [];
    public virtual IReadOnlyList<string> WritesArtifacts => [];

    public virtual string? PromptTemplate => null;   // filename in src/SpecRunner/prompts/
    public virtual string? IteratesOver   => null;   // artifact id of a frozen iteration list
    public virtual StepGuard? Guard       => null;   // whether this step applies at all
    public virtual bool RaisesDecision    => false;  // true if it blocks for a human

    public abstract void Execute(StepContext context);
}
```

**Step ids** are explicit string constants of the form `phase-<n>/task-<n>/step-<n>/<name>`,
where `<name>` is lowercase with hyphens. They are never derived from class names or reflection
order. Records are keyed by them, and the id maps directly onto the record directory tree, so
renaming one is a deliberate act that invalidates that step and everything downstream.

### 3.2 What `context` gives you

Reading:

| Call | What it does |
| --- | --- |
| `context.State.Get("name")` | A declared variable. Throws if undeclared. |
| `context.State.GetAll("name")` | Every value of an iterated variable, in frozen-list order. Declare it as `"name[]"`. |
| `context.ArtifactBody("id")` | The body of a declared read artifact. |
| `context.ArtifactPath("id")` | Its project-relative path. **Declared reads only.** |
| `context.AllArtifactBodies("id")` | `(Target, Body)` for every version an iterating producer wrote, in frozen-list order. |
| `context.IterationTarget` | The current item's identity, or `null` when the step does not iterate. |

Writing:

| Call | What it does |
| --- | --- |
| `context.CommitVariable("name", value)` | Commits a declared output variable. |
| `context.WriteArtifact("id", body)` | Writes a new version with a full origin header. **Returns the reference — keep it.** |
| `context.WriteArtifact("id", body, call)` | Same, with the model call's provenance folded into the header. Pass `call` on every model-derived artifact. |
| `context.WriteArtifact("id", body, extraHeader: doc => ...)` | Same, plus structured front matter of your own. A frozen iteration list is the one case in practice — see `FrozenIterationList.Describe`. |
| `context.SetSummary("...")` | Prose that lands in the completion record. |

The model, decisions, and branches:

| Call | What it does |
| --- | --- |
| `context.CallModel((name, value, source), ...)` | Loads the declared template, substitutes strictly, calls the model. |
| `context.ParseResponse(call)` | Runs the parser the template declared. A separate act from the call. |
| `context.AwaitDecision(question, options, codeLocation, related)` | Blocks until a human answers. |
| `context.RecordBranch(...)` | Writes the decision record for a branch. |
| `context.ConfirmDefect(upstreamStep, raisedBy, findingPath)` | Hands a confirmed defect to the invalidation cascade. |
| `context.ProducerOf("artifactId")` | The step id that declares an artifact as its output. |

And the lower-level surface the three tables above are built on. You need it less often, but the
worked example reaches for the first two:

| Call | What it does |
| --- | --- |
| `context.Artifacts.Read(path)` | Reads any artifact file by path, returning its parsed header, body, and both body hashes. Use it when you need an artifact's *header*, not just its body — reading a frozen iteration list is the usual reason. |
| `context.Paths.Absolute(rel)` / `.Relative(abs)` | Converts between project-relative paths (what records store) and absolute ones (what `Canonical.HashFile` wants). |
| `context.Note(kind, message, fields)` | Emits to the browser console. The only way a step says anything — see §7. |
| `context.Records` | The record store, if you need to read records a step wrote earlier. |
| `context.Inputs` | The declared inputs the engine computed for this execution, already hashed. |

`context.Artifacts.Read` and `context.Paths` are unrestricted — they take paths, not declared
ids, so they will happily read something this step never declared. That does not make it an
input: only declarations feed the graph and the completion record. If you find yourself reading
an artifact this way to *use its content*, declare it and use `ArtifactBody` instead.

### 3.3 Variables

Variables are strings. Single-assignment within a run: writing one that already holds a value is
a halt.

Variables produced inside an iteration are **namespaced by the iteration target** automatically —
you commit `note_headline` and the record shows `note_headline@change-002`. You never write the
`@` yourself.

A step that does not iterate reads all of them at once with the `[]` suffix:

```csharp
public override IReadOnlyList<string> ReadsVariables => ["note_headline[]"];
// ...
IReadOnlyList<string> headlines = context.State.GetAll("note_headline");
```

The order is the frozen list's order, which is the same order `AllArtifactBodies` returns — so
the two line up index for index.

### 3.4 Guards

A guard decides whether a step applies at all:

```csharp
public override StepGuard? Guard => new(
    "the review suspected an upstream defect",
    ["review_outcome"],                                  // must also be in ReadsVariables
    state => state.Get("review_outcome") == RouteReviewStep.DefectSuspected);
```

A guarded step that does not apply is reported in the console as `not-applicable` with the
predicate that decided it — never silently absent.

Two rules the startup check enforces: anything consuming a guarded step's output must itself be
guarded, and a guard may only read single values, never collections.

---

## 4. Prompt templates

One file per model call, in `src/SpecRunner/prompts/`. YAML front matter over a template body.

They live with the code rather than with the project data, because that is what they are: a step
names its template as a compiled string constant, and the template's hash is one of that step's
declared inputs (§4.3 below). Rename a template and a specific class stops working; edit one and
the step that uses it invalidates. You will be editing them in the same sitting as
`MethodologySteps.cs`.

```yaml
---
model: gpt-4o-mini
temperature: 0.0
top_p: 1.0
max_tokens: 2000
seed: 20260806
timeout_seconds: 120
parser: numbered-list
output_variables:
  - items
  - count
---
```

**All eight keys are required and no others are permitted.** The front matter is the complete and
only source of call configuration — nothing is merged in from the application. A missing key is a
halt naming the key and the file. An *unknown* key is also a halt, because an unknown key is
almost always a typo, and a typo that silently becomes a default is invisible.

### 4.1 Placeholders

`{{ var_name }}`, with optional interior whitespace.

| You write | You get |
| --- | --- |
| `{{ x }}` | the value of `x` |
| `\{{` | a literal `{{`, backslash consumed |
| `\\{{x}}` | a literal `\` followed by the value of `x` |
| `\\\{{x}}` | a literal `\` followed by a literal `{{` |
| `}}` alone | just text — the parser only looks for matched pairs |

Those middle rows look asymmetric, but they are one rule: **a run of backslashes immediately
before an opening delimiter pairs up into literal backslashes, C-style, and a leftover odd
backslash escapes the delimiter and is consumed.** So an odd-length run suppresses substitution
and an even-length run does not, and you can extend the table indefinitely without being told
what the next row is. Backslashes anywhere else — including in a substituted *value* — are never
touched.

Every row above has a check with a matching name in `tests/SpecRunner.Checks` (§8), so you can
read the rule and then read the thing that proves it.

Substitution is strict **in both directions**:

- An unresolved placeholder is a halt.
- A supplied variable the template never uses is a halt.
- A variable resolving to empty or whitespace is a halt.

Any of those, if silent, would produce a plausible-looking prompt that is wrong. That is the
worst failure available here, so none of them are silent.

You supply values with their source, which is recorded:

```csharp
var call = context.CallModel(
    ("changelog", context.ArtifactBody("changelog"), "phase-1/task-1/step-1/seed-changelog"));
```

### 4.2 Parsers

The parser is chosen by the `parser:` declaration and **never** by inspecting the response.
Inspecting the response to decide how to interpret the response is exactly the holistic
interpretation this application refuses. There is no lenient mode and no salvage.

| Parser | Grammar | Produces |
| --- | --- | --- |
| `whole-markdown` | The whole response is the body. | `content` |
| `numbered-list` | Every non-blank line is `N. text`, numbered contiguously from 1. | `items` (JSON array), `count` |
| `verdict` | Front matter with exactly `verdict` (`pass` or `upstream-defect-suspected`) and `suspected_artifact`, over prose. | `verdict`, `suspected_artifact`, `rationale` |

`output_variables` must match what the parser actually produced, **in both directions**. A
mismatch either way is a halt.

Adding a parser means adding a class to `OutputParsers.cs`. Keep the grammar strict: the raw
response is always on disk, so failing loudly costs you nothing you cannot inspect.

### 4.3 What the client will refuse

Worth knowing before you write a prompt that fights it:

- Only `finish_reason: stop` with non-empty content is accepted. `length` is a **halt**, not a
  truncated artifact — so set `max_tokens` generously.
- Refusal, content filter, empty content, multiple choices, or a missing choices array: halt.
- A response containing `tool_calls`: hard halt with the raw body kept. Not an unsupported
  feature — a violated invariant.
- Retries happen only for transport failure (connection reset, timeout, 429, 5xx) and are
  byte-identical resends. A parse failure is **never** retried. There is deliberately no
  reparse-or-reprompt loop, because a repair loop is the model influencing what runs next.

---

## 5. Worked example: release notes

We will build a methodology that turns a changelog into release notes. Three stages, each one
runnable on its own.

It has the same skeleton as the placeholder methodology that ships in the repo. That is not
laziness — those five mechanisms *are* what the engine offers, and seeing them assembled a second
time in a different domain is the fastest way to learn where the seams are.

Everything below was compiled and run end to end before being written down.

### Stage 1 — a seed, a decision, and one model call

**The seed step.** The project directory starts empty; the workflow creates its own starting
files.

```csharp
public sealed class SeedChangelogStep : Step
{
    public override string Id => "phase-1/task-1/step-1/seed-changelog";
    public override string Description => "Create the changelog stub for the operator to fill in.";
    public override IReadOnlyList<string> WritesArtifacts => ["changelog"];

    public override void Execute(StepContext context)
    {
        context.WriteArtifact("changelog",
            """
            # Changelog

            Replace this stub with the real changelog, one change per line.

            - Sessions now expire after 30 minutes of inactivity
            - The export button accepts a date range
            - Fixed a crash when opening an empty project
            """);

        context.SetSummary("Wrote the changelog stub.");
    }
}
```

When you replace that stub by hand, the next run notices the body no longer matches the hash in
the file's own origin header, reports it as a **hand-edit** rather than as staleness, takes what
you wrote as truth, and invalidates everything derived from the stub. The seed step itself is not
re-run — your edit stands.

**The decision step.** Note `RaisesDecision => true`; without it the application halts and tells
you, because that declaration is what puts the answer record into the step's inputs.

```csharp
public sealed class ConfirmChangelogStep : Step
{
    public const string Ready    = "changelog-is-ready";
    public const string NotReady = "changelog-is-not-ready";

    public override string Id => "phase-1/task-1/step-2/confirm-changelog";
    public override string Description => "Ask whether the changelog is complete.";
    public override bool RaisesDecision => true;
    public override IReadOnlyList<string> ReadsArtifacts  => ["changelog"];
    public override IReadOnlyList<string> WritesVariables => ["changelog_confirmed"];

    public override void Execute(StepContext context)
    {
        var answer = context.AwaitDecision(
            "Is the changelog complete enough to write release notes from?",
            [Ready, NotReady],
            Id,
            [context.ArtifactPath("changelog")]);

        switch (answer)
        {
            case Ready:
                context.CommitVariable("changelog_confirmed", Ready);
                context.SetSummary("The operator confirmed the changelog is complete.");
                return;

            case NotReady:
                throw new HaltException(
                    "The operator answered that the changelog is not complete. Edit " +
                    $"{context.ArtifactPath("changelog")}, then run again.") { StepId = Id };

            default:
                throw new HaltException($"Answer '{answer}' is outside the closed enum for '{Id}'.") { StepId = Id };
        }
    }
}
```

The `default` arm is a halt, never a fallthrough. Write it every time, even when you believe the
enum is exhaustive — it is what turns a malformed record into a diagnosis instead of a surprise.

**The model call.**

```csharp
public sealed class ExtractChangesStep : Step
{
    public override string Id => "phase-1/task-2/step-1/extract-changes";
    public override string Description => "Extract the individual changes from the changelog.";
    public override string? PromptTemplate => "extract-changes.md";

    public override IReadOnlyList<string> ReadsArtifacts  => ["changelog"];
    public override IReadOnlyList<string> ReadsVariables  => ["changelog_confirmed"];
    public override IReadOnlyList<string> WritesArtifacts => ["change-list"];
    public override IReadOnlyList<string> WritesVariables => ["change_items", "change_count"];

    public override void Execute(StepContext context)
    {
        // Reading changelog_confirmed is what makes the human decision an actual dependency of
        // this step, rather than a formality that happened earlier in the sequence.
        _ = context.State.Get("changelog_confirmed");

        var call = context.CallModel(
            ("changelog", context.ArtifactBody("changelog"), "phase-1/task-1/step-1/seed-changelog"));

        var parsed = context.ParseResponse(call);

        context.WriteArtifact("change-list", call.Result.Content, call);
        context.CommitVariable("change_items", parsed["items"]);
        context.CommitVariable("change_count", parsed["count"]);
        context.SetSummary($"Extracted {parsed["count"]} change(s).");
    }
}
```

Passing `call` into `WriteArtifact` is what fills the artifact's origin header with the model
requested, the model the server reported, the sampling parameters, the resolved-prompt hash, the
response id, the system fingerprint, and the token usage. Do it on every model-derived artifact.

`src/SpecRunner/prompts/extract-changes.md`:

```
---
model: gpt-4o-mini
temperature: 0.0
top_p: 1.0
max_tokens: 1000
seed: 20260806
timeout_seconds: 60
parser: numbered-list
output_variables:
  - items
  - count
---

Extract the individual changes from the changelog below.

Output format — a strict grammar; anything else is rejected:

- Every line is `N. <change>`, numbered contiguously from 1, one line per change.
- No preamble, no headings, no bullets, no closing remarks.

## Changelog

{{ changelog }}
```

Wire the three steps up and you have a runnable methodology:

```csharp
public static IReadOnlyList<Step> Steps() =>
[
    new SeedChangelogStep(),
    new ConfirmChangelogStep(),
    new ExtractChangesStep()
];
```

### Stage 2 — freeze a set, then iterate over it

You never iterate over a live directory scan or over live state. You **materialize the set to
disk and freeze it** first, as an ordered numbered artifact with its own origin header. Iteration
then runs against that frozen list, and the list records the artifact it came from plus that
artifact's hash — which is what makes drift detectable.

```csharp
public sealed class FreezeChangeSetStep : Step
{
    public override string Id => "phase-1/task-2/step-2/freeze-change-set";
    public override string Description => "Freeze the change list into an ordered iteration set.";

    public override IReadOnlyList<string> ReadsArtifacts  => ["change-list"];
    public override IReadOnlyList<string> ReadsVariables  => ["change_items"];
    public override IReadOnlyList<string> WritesArtifacts => ["change-set"];

    public override void Execute(StepContext context)
    {
        var texts = JsonSerializer.Deserialize<List<string>>(context.State.Get("change_items"))
            ?? throw new HaltException("change_items did not deserialize to a list.");

        var sourcePath = context.ArtifactPath("change-list");
        var sourceHash = Canonical.HashFile(context.Paths.Absolute(sourcePath));

        var items = new List<IterationItem>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            items.Add(new IterationItem(i + 1, $"change-{i + 1:D3}", texts[i]));
        }

        context.WriteArtifact(
            "change-set",
            FrozenIterationList.RenderBody(items, sourcePath, sourceHash),
            extraHeader: doc => FrozenIterationList.Describe(doc, sourcePath, sourceHash, items));

        context.SetSummary($"Froze {items.Count} change(s).");
    }
}
```

The `IterationItem` identity (`change-001`) becomes a directory name, so keep it stable and
filesystem-safe. Ordinal position is data *inside* the list, never a directory name — reordering
the set must not silently re-point existing records.

Now the iterating step. Declare `IteratesOver`, and declare the same artifact in `ReadsArtifacts`
— the frozen list is an input like any other and has to be hashed as one.

```csharp
public sealed class WriteNoteStep : Step
{
    public override string Id => "phase-2/task-1/step-1/write-note";
    public override string Description => "Write the release note for one change.";
    public override string? PromptTemplate => "write-note.md";
    public override string? IteratesOver => "change-set";

    public override IReadOnlyList<string> ReadsArtifacts  => ["change-set"];
    public override IReadOnlyList<string> WritesArtifacts => ["change-note"];
    public override IReadOnlyList<string> WritesVariables => ["note_headline"];

    public override void Execute(StepContext context)
    {
        var target = context.IterationTarget
            ?? throw new HaltException($"'{Id}' iterates but ran with no iteration target.");

        var list = FrozenIterationList.Read(context.Artifacts.Read(context.ArtifactPath("change-set")));
        var item = list.Items.FirstOrDefault(i => i.Identity == target)
            ?? throw new HaltException($"'{target}' is not in the frozen list {list.ArtifactPath}.");

        var call = context.CallModel(("change", item.Text, "phase-1/task-2/step-2/freeze-change-set"));
        var parsed = context.ParseResponse(call);

        context.WriteArtifact("change-note", parsed["content"], call);
        context.CommitVariable("note_headline", item.Text);
        context.SetSummary($"Wrote the note for {item.Identity} ({item.Ordinal} of {list.Items.Count}).");
    }
}
```

You write the step as if it handles one item. The engine runs it once per item, keyed by target,
so an interrupted iteration resumes at the exact item — and between items it re-hashes the
artifact the set came from. If that changed mid-run, the run **halts** rather than finishing
against a list nobody can reconstruct.

### Stage 3 — a verdict, a branch, and backward flow

A defect found downstream is treated as evidence of a defect upstream until proven otherwise.
Three pieces: a validator that emits a machine-checkable verdict, a branch that reads only that
field, and a confirmation before anything rewinds.

**The validator** just writes the verdict artifact. It decides nothing.

```csharp
public sealed class ReviewNotesStep : Step
{
    public override string Id => "phase-2/task-2/step-1/review-notes";
    public override string Description => "Check the release notes against the changelog.";
    public override string? PromptTemplate => "review-notes.md";

    public override IReadOnlyList<string> ReadsArtifacts  => ["changelog", "change-note"];
    public override IReadOnlyList<string> WritesArtifacts => ["note-review"];
    public override IReadOnlyList<string> WritesVariables => ["review_verdict", "review_suspect"];

    public override void Execute(StepContext context)
    {
        var notes = new StringBuilder();
        foreach (var (target, body) in context.AllArtifactBodies("change-note"))
        {
            notes.Append("<!-- ").Append(target).Append(" -->\n\n").Append(body).Append("\n\n");
        }

        var call = context.CallModel(
            ("changelog", context.ArtifactBody("changelog"), "phase-1/task-1/step-1/seed-changelog"),
            ("notes", notes.ToString(), Id));

        var parsed = context.ParseResponse(call);

        context.WriteArtifact("note-review", call.Result.Content, call);
        context.CommitVariable("review_verdict", parsed["verdict"]);
        context.CommitVariable("review_suspect", parsed["suspected_artifact"]);
        context.SetSummary($"Review verdict: {parsed["verdict"]}.");
    }
}
```

**The branch.** This is where the whole design either holds or doesn't, so read it closely.

```csharp
public sealed class RouteReviewStep : Step
{
    public const string Accepted        = "accepted";
    public const string DefectSuspected = "defect-suspected";

    // The closed set of upstream artifacts a finding may name. The model produces a string;
    // this list is what makes that string routable. Anything outside it is a halt.
    public static readonly IReadOnlyList<string> RoutableUpstreamArtifacts =
    [
        .. HandleChangelogDefectStep.RegenerableUpstreamArtifacts,
        .. HandleChangelogDefectStep.OperatorAuthoredUpstreamArtifacts
    ];

    public override string Id => "phase-2/task-2/step-2/route-review";
    public override string Description => "Branch on the review verdict.";
    public override IReadOnlyList<string> ReadsVariables  => ["review_verdict", "review_suspect"];
    public override IReadOnlyList<string> WritesVariables => ["review_outcome"];

    public override void Execute(StepContext context)
    {
        var verdict = context.State.Get("review_verdict");

        var outcome = verdict switch
        {
            OutputParsers.VerdictPass                     => Accepted,
            OutputParsers.VerdictUpstreamDefectSuspected  => DefectSuspected,
            _ => throw new HaltException($"Verdict '{verdict}' is outside the closed enum.") { StepId = Id }
        };

        if (outcome == DefectSuspected)
        {
            var suspect = context.State.Get("review_suspect");
            if (!RoutableUpstreamArtifacts.Contains(suspect))
            {
                throw new HaltException(
                    $"The reviewer named '{suspect}', which is not in the routable set " +
                    $"({string.Join(", ", RoutableUpstreamArtifacts)}).") { StepId = Id };
            }
        }

        context.RecordBranch(
            predicateIdentity: $"{Id}: switch on review_verdict",
            evaluatedInput: "review_verdict",
            evaluatedValue: verdict,
            options: OutputParsers.Verdicts,
            chosenBranch: outcome,
            nextStep: outcome == Accepted
                ? "phase-3/task-1/step-1/assemble-release-notes"
                : "phase-2/task-2/step-3/handle-changelog-defect",
            explanation: "Code read the verdict field. The reviewer's prose was not consulted.");

        context.CommitVariable("review_outcome", outcome);
        context.SetSummary($"Verdict '{verdict}' routed to '{outcome}'.");
    }
}
```

Two things are doing real work here. The `switch` reads one enum field and nothing else — not the
prose, not the tone, not "what the reviewer seems to mean". And `RoutableUpstreamArtifacts` means
the model cannot point the invalidation cascade at something you did not sanction, even if it
names one.

**The finding and the rewind.**

Before the code: there is a trap here, and it is worth understanding before you write your own
version.

Invalidating a step means it will **re-execute**. That is the only thing invalidation means, and
it is uniform — the engine has no concept of "re-run this but keep the old output". So pointing a
cascade at a step whose artifact a *person* wrote destroys their work: the step runs again, writes
a fresh stub as `v2`, and `v2` becomes the artifact in force. (The edited `v1` is still on disk,
because nothing is ever deleted, and `v2`'s header records `supersedes_version: 1` — but
downstream now consumes the stub.)

So upstream artifacts come in two classes, and this step keeps them apart:

- **Regenerable** — a step can rebuild it from its own inputs. Cascade freely.
- **Operator-authored** — a person wrote the content. Never cascade; halt and let them correct it.
  The ordinary hand-edit path then invalidates everything derived from it automatically, through
  the same cascade, without this step having to ask.

```csharp
public sealed class HandleChangelogDefectStep : Step
{
    public const string Confirm = "confirm-changelog-defect";
    public const string Reject  = "reject-finding";

    /// Upstream artifacts a step can rebuild from its own inputs. Safe to cascade into.
    public static readonly IReadOnlyList<string> RegenerableUpstreamArtifacts = ["change-list"];

    /// Upstream artifacts whose content a person wrote. Invalidating the producer would re-run
    /// the step that seeded them and supersede the operator's content with a fresh stub.
    public static readonly IReadOnlyList<string> OperatorAuthoredUpstreamArtifacts = ["changelog"];

    public override string Id => "phase-2/task-2/step-3/handle-changelog-defect";
    public override string Description => "Raise a defect finding and, if confirmed, act on it.";
    public override bool RaisesDecision => true;

    public override StepGuard? Guard => new(
        "the review suspected an upstream defect",
        ["review_outcome"],
        state => state.Get("review_outcome") == RouteReviewStep.DefectSuspected);

    public override IReadOnlyList<string> ReadsArtifacts  => ["note-review", "changelog"];
    public override IReadOnlyList<string> ReadsVariables  => ["review_outcome", "review_suspect"];
    public override IReadOnlyList<string> WritesArtifacts => ["changelog-defect"];
    public override IReadOnlyList<string> WritesVariables => ["defect_decision"];

    public override void Execute(StepContext context)
    {
        var suspect = context.State.Get("review_suspect");
        var upstreamStep = context.ProducerOf(suspect);

        // WriteArtifact returns the reference. Use it: ArtifactPath resolves *declared reads*,
        // and a step does not read what it writes.
        var finding = context.WriteArtifact("changelog-defect",
            $"""
             # Defect finding

             - Suspected upstream artifact: `{suspect}`
             - Produced by step: `{upstreamStep}`
             - Raised by: `phase-2/task-2/step-1/review-notes`
             - Evidence: `{context.ArtifactPath("note-review")}`

             ## The reviewer's evidence, verbatim

             {context.ArtifactBody("note-review")}
             """);

        var answer = context.AwaitDecision(
            $"The reviewer suspects a defect in '{suspect}'. Confirm, and invalidate it along with "
            + "everything derived from it?",
            [Confirm, Reject],
            Id,
            [finding.Path]);

        context.CommitVariable("defect_decision", answer);

        switch (answer)
        {
            case Reject:
                context.SetSummary("The operator rejected the finding; the chain stands.");
                return;

            case Confirm when OperatorAuthoredUpstreamArtifacts.Contains(suspect):
                context.SetSummary($"Confirmed defect in operator-authored '{suspect}'. Halted for correction.");
                throw new HaltException(
                    $"The defect is in '{suspect}', which you wrote. This step will not invalidate " +
                    $"'{upstreamStep}': re-running it would supersede your content with a fresh stub.\n\n" +
                    $"Edit {context.ArtifactPath(suspect)} yourself and run again. The edit is taken as truth, " +
                    "and everything derived from it is invalidated automatically.") { StepId = Id };

            case Confirm:
                context.ConfirmDefect(upstreamStep, "phase-2/task-2/step-1/review-notes", finding.Path);

                throw new HaltException(
                    $"Backward flow applied: '{upstreamStep}' and everything derived from it are invalidated. "
                    + "Run again to re-establish the chain from the corrected source.") { StepId = Id };

            default:
                throw new HaltException($"Answer '{answer}' is outside the closed enum for '{Id}'.") { StepId = Id };
        }
    }
}
```

Backward flow is **not a rewind engine**. `ConfirmDefect` runs the same invalidation cascade as
any other invalidation, then the step halts — because position is derived, so the way to act on
an invalidation is to run the process again. Replay skips whatever is still in force and
re-establishes the chain from the corrected source.

Both branches were run. Naming `change-list` writes one `defect-confirmed` invalidation plus six
`upstream-cascade` ones and leaves the changelog untouched. Naming `changelog` writes **no**
invalidation records at all and halts with the correction instructions — which is what you want,
because the operator's next action (editing the file) is itself what triggers the invalidation.

There is a safety valve you get for free: if the same downstream validator invalidates the same
upstream target more than three times, the run halts and prints the full revision history instead
of looping. A pair that cannot converge is a judgment the application does not make.

**The report.** Note that it carries no guard, even though the step before it does. That is
deliberate, and it is worth being explicit about why, because the startup check enforces a rule
that sounds like it should bite here.

The rule is *"anything consuming a guarded step's output must itself be guarded"* — and it is
about **consumption**, not about sequence. Assembly reads `change-note`, `note-review`,
`change_count`, `review_outcome` and `note_headline[]`, and every one of those comes from an
ungated step; `review_outcome` in particular comes from `RouteReviewStep`, which always runs.
Nothing consumes `defect_decision` or `changelog-defect`. So the rule never engages.

That generalises into a useful discipline: **a guarded step's outputs should be either terminal
or consumed only by equally guarded steps.** If you find yourself wanting an ungated step to read
one, that is the design telling you the guard belongs further down.

Assembly is also not an edge case. On the ordinary `pass` path the guarded step is simply
`not-applicable` and assembly runs as usual — it is skipped only when a confirmed defect halts
the run before reaching it.

Here it is, showing a collection read lining up with the artifacts:

```csharp
public sealed class AssembleReleaseNotesStep : Step
{
    public override string Id => "phase-3/task-1/step-1/assemble-release-notes";
    public override string Description => "Assemble the finished release notes.";

    public override IReadOnlyList<string> ReadsArtifacts => ["change-note", "note-review"];
    public override IReadOnlyList<string> ReadsVariables =>
        ["change_count", "review_outcome", "note_headline[]"];
    public override IReadOnlyList<string> WritesArtifacts => ["release-notes"];

    public override void Execute(StepContext context)
    {
        var headlines = context.State.GetAll("note_headline");
        var notes = context.AllArtifactBodies("change-note");

        var body = new StringBuilder();
        body.Append("# Release notes\n\n")
            .Append($"- Changes: {context.State.Get("change_count")}\n")
            .Append($"- Review outcome: {context.State.Get("review_outcome")}\n\n");

        for (var i = 0; i < notes.Count; i++)
        {
            body.Append($"<!-- {notes[i].Target}: {headlines[i]} -->\n\n").Append(notes[i].Body).Append("\n\n");
        }

        context.WriteArtifact("release-notes", body.ToString());
        context.SetSummary($"Assembled {notes.Count} note(s).");
    }
}
```

`GetAll` and `AllArtifactBodies` both return frozen-list order, so indexing them together is
safe. That is a guarantee, not a coincidence.

### The finished list

```csharp
public static IReadOnlyList<Step> Steps() =>
[
    new SeedChangelogStep(),
    new ConfirmChangelogStep(),
    new ExtractChangesStep(),
    new FreezeChangeSetStep(),
    new WriteNoteStep(),
    new ReviewNotesStep(),
    new RouteReviewStep(),
    new HandleChangelogDefectStep(),
    new AssembleReleaseNotesStep()
];
```

Nine steps, five model calls on a three-item changelog, two human decisions, one branch.

---

## 6. What lands on disk

Everything, as Markdown you can open without the application running. These are real files from
running the example above.

### An artifact

`artifacts/change-note/change-002/v1.md` — the origin header is complete enough that a person
holding this one file never has to go hunting:

```
---
artifact_id: change-note
artifact_version: 1
producing_step_id: phase-2/task-1/step-1/write-note
iteration_target: change-002
run_id: run-20260807T031401Z-e82412
sequence: 87
timestamp_utc: 2026-08-07T03:14:06.986Z
hash_algorithm: SHA-256
canonicalization_version: 1
prompt_template_path: prompts/write-note.md
prompt_template_hash: sha256:dad139f651dbfd5fb4ed2297f3fba23275b6a9d946f6cc8f0cad58363db52dc3
resolved_prompt_hash: sha256:9ef4e5d0d1e3dd5676c51c855759f61d24f3cd390a6cd87fece24471fbff341e
model_requested: stub-model
model_reported: stub-model
temperature: 0
top_p: 1
max_tokens: 1000
seed: 20260806
response_id: chatcmpl-stub
system_fingerprint: fp_stub_0001
usage_prompt_tokens: 83
usage_completion_tokens: 48
usage_total_tokens: 131
raw_response_record: records/phase-2/task-1/step-1/write-note/change-002/llm-response.run-20260807T031401Z-e82412.s00085.md
supersedes_version: "-"
superseded_because: "-"
inputs:
  - kind: file
    name: prompts/write-note.md
    hash: sha256:dad139f651dbfd5fb4ed2297f3fba23275b6a9d946f6cc8f0cad58363db52dc3
    producer: template
  - kind: file
    name: artifacts/change-set/v1.md
    hash: sha256:aa078238ba702fdc5f3f5bf2541faa07c2e5708bd5c79c03733d55b5b2e8ea73
    producer: phase-1/task-2/step-2/freeze-change-set
body_hash: sha256:56c42555b9fa2e698720e39c89f581e9766e6272f6ccebd109ee2a48af3f315f
---

## Date-range export

### What changed

A concise description of the change, written for someone upgrading.
...
```

Note that the recorded template name is `prompts/write-note.md` while the file physically lives at
`src/SpecRunner/prompts/write-note.md`. That is deliberate: `prompts/` is the template *namespace*,
not a path relative to anything. The directory is configurable, so a real path would differ between
operators running the same workflow and make two otherwise identical records disagree. The hash
identifies the content; the startup config echo says where the namespace resolved.

`body_hash` covers the body alone; when the body stops matching it, a person edited the file.
`model_reported` sitting beside `model_requested` is how you find out a provider quietly served
you something else — that difference is also called out in the console as a named condition.

### A completion record

`records/phase-2/task-1/step-1/write-note/change-002/completion.<run-id>.s00089.md`:

```
---
record_kind: completion
step_id: phase-2/task-1/step-1/write-note
iteration_target: change-002
run_id: run-20260807T031401Z-e82412
sequence: 89
timestamp_utc: 2026-08-07T03:14:07.001Z
hash_algorithm: SHA-256
canonicalization_version: 1
inputs:
  - kind: file
    name: prompts/write-note.md
    hash: sha256:dad139f651dbfd5fb...
    producer: template
  - kind: file
    name: artifacts/change-set/v1.md
    hash: sha256:aa078238ba702fdc5f...
    producer: phase-1/task-2/step-2/freeze-change-set
output_variables:
  - name: note_headline@change-002
    value: "The export button accepts a date range"
    hash: sha256:5fef2a116a576e39e1...
output_artifacts:
  - artifact_id: change-note
    iteration_target: change-002
    version: 1
    path: artifacts/change-note/change-002/v1.md
    hash: sha256:6a6a00c4dc9bac2e4f...
---

Wrote the note for change-002 (2 of 3).
```

That input list is what the next run re-computes and compares. Note the automatic `@change-002`
namespacing on the variable, and that the prompt template is an input — so editing a template
invalidates the steps that use it.

### A decision record

Every branch, on disk:

```
---
record_kind: decision
step_id: phase-2/task-2/step-2/route-review
predicate: "phase-2/task-2/step-2/route-review: switch on review_verdict"
evaluated_input: review_verdict
evaluated_value: upstream-defect-suspected
options:
  - pass
  - upstream-defect-suspected
chosen_branch: defect-suspected
next_step: phase-2/task-2/step-3/handle-changelog-defect
---

Code read the verdict field. The reviewer's prose was not consulted.
```

Holding only the code and this file, you can re-derive the branch. That is the point.

### The tree

```
project/
  artifacts/<artifact-id>/[<target>/]v<n>.md
  records/<phase>/<task>/<step>/<name>/[<target>/]
      completion.<run-id>.s<seq>.md
      invalidation.<run-id>.s<seq>.md
      question.v<n>.md  /  answer.v<n>.md
      decision.<run-id>.s<seq>.md
      llm-request.<run-id>.s<seq>.md      (written BEFORE the request is sent)
      llm-attempt.<run-id>.s<seq>.a<n>.md (every attempt, not just the successful one)
      llm-response.<run-id>.s<seq>.md     (the raw body, byte for byte)
  runs/<run-id>.md                        (everything the console showed)
  state/project-state.md                  (a projection; never an input)
  notes/                                  (yours; never read, never validated)
```

Nothing is ever overwritten and **nothing is ever deleted** — there is no code path in the
application that deletes a file. Superseding is additive: a new record lands beside the old one
and an invalidation record names which one it retired.

Anything in this tree that is not a `.md` file the workflow produced **halts the run**, with two
exceptions: a checked-in allowlist of directories it does not own (`.git`, `.hg`, `.svn`, `.vs`,
`.vscode`, `.idea`), and the note convention — anything under `notes/`, and any file named
`*.note.md`.

---

## 7. Rules the startup check enforces

The graph is validated before the web server binds. A defect is a startup crash naming the
offending ids, not a surprise on step 340. It will reject:

| Rule | Message you get |
| --- | --- |
| Step ids match `phase-<n>/task-<n>/step-<n>/<name>` | `does not match the required form` |
| No duplicate step ids | `duplicate step id` |
| Every consumed variable has exactly one producer | `which no step declares as an output` |
| No variable or artifact has two producers | `has two producers` |
| Producers precede consumers in sequence | `does not precede it in sequence` |
| No cycles | `cycle in the dependency graph through` |
| An iterating step declares its list in `ReadsArtifacts` too | `does not declare it as a read artifact` |
| Reading an iterated variable singly requires iterating the same list, or the `[]` form | `Either iterate over the same list, or declare the read as 'name[]'` |
| A collection read requires an iterating producer | `does not iterate and commits a single value` |
| Guard variables are also in `ReadsVariables` | `which the step does not declare as a read` |
| Guards read single values only | `Guards may read single values only` |
| Consumers of a guarded step are themselves guarded | `is guarded and may not run` |

There is one more check that is not about the graph: **the two-surface rule**. Every diagnostic
must go through `Emit.To(Surface.Terminal, ...)` or `Emit.To(Surface.Console, ...)`. A
`Console.Write`, an `ILogger`, or an `AddConsole` anywhere outside the two files that implement
those surfaces fails the build with `SURFACE001`. Workflow state goes to the console; startup and
process-ending events go to the terminal. Use `context.Note(kind, message, fields)` from a step.

---

## 8. Testing a methodology without spending money

Three techniques, in increasing order of usefulness.

**Run the checks.** The rules this document states as a specification — the escape table,
substitution strictness, canonicalization, deterministic serialization, and every parser
grammar — have executable checks against the real code:

```bash
dotnet run --project tests/SpecRunner.Checks
```

58 checks, no package restore, exit code 0 or 1. Check names match the documented rules, so if
you doubt a claim here, that is where to look — and if you add a parser, that is where to prove
its grammar does what you said.

**Read the plan without running.** Start the application and look at the pre-flight plan before
pressing start. It lists every step with `skip` / `execute` / `not-applicable` and the reason, a
count of model calls, and any orphaned artifacts or hand-edits it found. Most declaration
mistakes are visible there, and the graph validator catches the rest at startup.

**Point it at a stub endpoint.** `baseUrl` is just configuration. A ~100-line HTTP listener that
serves OpenAI-style SSE and returns canned replies chosen by a marker in the prompt exercises
your entire pipeline — streaming, parsing, iteration, branching, backward flow — in seconds, for
free, deterministically.

```json
{ "projectDirectory": "project", "promptsDirectory": "src/SpecRunner/prompts",
  "baseUrl": "http://127.0.0.1:8099/v1", "port": 5107 }
```

Your stub needs to emit, per call: a series of `data:` chunks carrying
`choices[0].delta.content`, a final chunk with `finish_reason: "stop"`, optionally a usage chunk
with an empty `choices` array, and `data: [DONE]`. That is the entire contract the client accepts.

The example in section 5 was verified exactly this way: happy path to completion, then a replay
with the stub switched off (which must skip everything and touch nothing), then a defect verdict
through confirmation and cascade.

**Also worth doing once**, because it is the property everything else rests on: kill the process
mid-run with Task Manager, then run it again. It should pick up exactly where it left off, and
report the interrupted model call as "initiated, never completed".

---

## 9. Mistakes that are easy to make

**Calling `ArtifactPath` for an artifact you write.** It resolves declared *reads* and throws
otherwise. Keep the reference `WriteArtifact` returns and use `.Path`. (This one bit during
authoring of the example.)

**Forgetting `RaisesDecision => true`.** The application halts and tells you. That flag is what
puts the answer record into the step's inputs — without it, editing an answer that is already in
force would be neither acted on nor reported.

**Expecting a changed answer to take effect.** It does not. A decision, once answered, is in
force; writing a different answer is a **conflict and a halt**. Revision is deliberate and
two-step: invalidate the decision first (console button, or delete the completion record), and
only then does the question re-open as a new numbered round. Superseded records stay on disk.

**Reading an iterated variable without `[]`.** Startup will tell you which form to use. Inside
the same iteration, plain is right; from outside, `[]` is right.

**Setting `max_tokens` too low.** `finish_reason: length` is a halt, not a truncated artifact.
The run stops rather than committing a half-written spec.

**Writing a prompt that asks the model to decide something.** It cannot. Have it emit a field
from a closed enum and branch on that field in code. If you catch yourself wanting the model to
pick the next step, that is the design telling you the decision belongs in a `switch` or in front
of a human.

**Expecting the model to fix its own bad output.** There is no reparse-or-reprompt loop, on
purpose. Tighten the prompt or loosen the parser — the raw response is always on disk to look at.

**Assuming a hand-edit will be re-generated over.** It will not. Edits are accepted as truth; the
producing step is not re-run, and what gets invalidated is everything *downstream*.

**Pointing a cascade at a step whose output a person wrote.** This is the sharpest edge in the
whole design, because the two behaviours look contradictory until you see what triggers each:

| Trigger | What it invalidates | Does the producing step re-run? |
| --- | --- | --- |
| Hand-edit | consumers, because *their* recorded input hash changed | **No** — the producer's own inputs are unchanged, so its record still stands |
| `ConfirmDefect` | the producer's completion record, directly | **Yes** — that record is exactly what was making it skip |

What short-circuits a step is its completion record, and `ConfirmDefect` retires precisely that.
So confirming a defect against an operator-authored artifact re-arms its seed step, and the next
run writes a stub `v2` that supersedes the edit. Keep a code-defined list of which upstream
artifacts are regenerable, as `HandleChangelogDefectStep` does, and halt for the human on the rest.

(The methodology that ships in the repo does not have this problem: it routes findings at
`requirements`, which a model call produces. The operator-authored `project-brief` is deliberately
not in its routable set.)

---

## Where things are

```
src/SpecRunner/
  Workflow/     Methodology.cs and MethodologySteps.cs — the two files you edit
                Step.cs, StepContext.cs — the API above
                FrozenIterationList.cs — iteration sets
  Llm/          PromptTemplate.cs (substitution), OutputParsers.cs (add parsers here)
                LlmClient.cs (call discipline)
  Reconcile/    the invalidation engine, the plan, the project scan
  Graph/        the startup validation whose messages are in section 7
  Records/      record and artifact shapes
  Engine/       the sequential runner
  Surfaces/     the two-surface emit API
  prompts/      your templates — a dependency of the classes that name them
tests/SpecRunner.Checks/
                executable checks for the rules in §4.1, §2.2, §6 and §4.2
```

Start by reading `MethodologySteps.cs`. Every mechanism in this document appears there once.

If you hit something in the engine that looks arbitrary, check
[implementation_decisions.md](implementation_decisions.md) before working around it — it records
which choices a design pillar forced, which resolve a genuine conflict between two requirements,
and which are arbitrary and safe to change. Guards (§4.1 there), the `RaisesDecision` declaration
(§4.2) and the collection-read ordering rule (§4.4) are the three you are most likely to meet
while authoring.
