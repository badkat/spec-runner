# Spec Runner

A Windows CLI application that hosts a local web server and opens a browser-based console to
execute one opinionated spec-driven development methodology from beginning to end.

The methodology is phases containing tasks containing steps. The step is the atomic unit, and
every step is deterministic C# — including the steps that call a language model, which is treated
purely as a stateless text transformer reached over an OpenAI-compatible HTTP API with no tools
and no influence over control flow.

```bash
dotnet run --project src/SpecRunner
```

Then answer the questions the console asks you.

**Writing your own methodology?** [AUTHORING.md](AUTHORING.md) is the guide: how a run works,
the Step API, prompt templates and parsers, and a complete worked example built from scratch. It
is standalone — you do not need to read this file first.

---

## Running it

Requires **.NET 10** and Windows.

```bash
setx SPECRUNNER_API_KEY sk-your-key-here
```

```bash
dotnet run --project src/SpecRunner
```

`dotnet run` always uses the repository root as its working directory (the project file sets
`RunWorkingDirectory`), so that command works from wherever you happen to be — it finds the
templates at `src/SpecRunner/prompts` and creates `project/` beside this README on first run.

If you launch the **built executable** instead, it uses whatever working directory you launch it
with, like any other CLI tool, so launch it from the repository root. When it cannot find the
templates it halts and prints the working directory it was standing in, which is usually the
answer.

Configuration is optional; copy `specrunner.config.example.json` to `specrunner.config.json` and
pass `--config` if you want to change anything — relative paths in it resolve against the config
file's own directory, so it is portable. Every resolved setting is printed to the terminal at
startup, with the API key elided.

| Flag | Effect |
| --- | --- |
| `--config <path>` | Use a specific JSON config file. |
| `--clear-stale-lock` | Proceed past a lock file whose process is no longer alive. |
| `--help` | Usage. |

The browser opens itself. If it does not, the terminal says so and prints the URL; the server is
listening either way.

---

## The two surfaces

There are exactly two places this application says anything, and each class of information has
exactly one of them. There is no log file you have to know about, no debug channel, no verbosity
flag.

**The terminal** owns everything that happens before the browser console can exist, and
everything that ends the process: startup self-checks, configuration, the dependency-graph
validation, port binding, unhandled exceptions with full stack traces, and the shutdown reason.
It says nothing about workflow state — not even to echo a mid-run block.

**The browser console** owns workflow state: the pre-flight plan, every step's start and commit,
every skip with the record that justified it, invalidations with their cause, model output
streaming token by token, blocks, and halts. Everything sent there is simultaneously appended to
a run log at `project/runs/<run-id>.md`, so closing the browser loses nothing, and that file — not
the terminal — is the recovery path for a block that happened while nobody was watching.

The rule is enforced at build time. A `Console.Write`, an `ILogger`, or an `AddConsole` anywhere
outside `TerminalSurface.cs` and `SurfaceLoggerProvider.cs` fails the build with `SURFACE001`.

---

## How resumption works

**Workflow position is not stored. It is derived.**

The workflow is plain sequential C# executed from the start of the project on every run. A step
whose result is already committed to disk short-circuits instead of re-executing. The filesystem
is not a checkpoint of position — it *is* the position. There is no serialized continuation and
no saved cursor.

Stopping is ending the process. Resuming is running it again.

### Replay is not blind skipping

Before a completion record is honored, the inputs it names must still match what is on disk.
Every record lists every input it consumed — file path plus content hash, variable name plus
value hash. **File modification times are not consulted anywhere in this application.**

Where the inputs no longer match, the record is stale, and it is invalidated along with
everything reachable downstream of it in the dependency graph. Reconciling records against the
graph is the normal path on every startup, not a repair operation.

### Hashing is over a declared canonical form

UTF-8, BOM stripped, CRLF → LF, trailing whitespace stripped per line, exactly one trailing
newline. Each record stores the algorithm name and a canonicalization version integer.

This is what makes hand-editing safe. A save from a different editor, or a git checkout with
different line-ending settings, does not silently nuke the pipeline. Changing the canonicalization
rules means bumping the version, which invalidates everything at once — loudly and on purpose.

### Two kinds of change, told apart

An artifact whose body no longer matches the hash recorded **in its own origin header** was edited
by a person. An artifact whose *inputs* changed is stale. Both take the on-disk content as truth
and invalidate downstream, but they are reported differently, because they mean different things
to you.

---

## Hard-kill is safe

**You can kill this process at any instant. Ctrl+C it, close the window, pull the power.**

That is a property the persistence design buys, and it is worth stating plainly:

1. Every file is written to a temp file and renamed into place. No record ever names a
   half-written file, including under disk-full or hard-kill.
2. A committing step orders its writes so the **artifact lands before the record** that marks the
   step complete.

A crash between the two leaves an artifact that no record names. The next run re-executes the
step and commits a new version alongside it. The unreferenced artifact stays on disk, read by
nothing, standing as an account of what the interrupted run produced — and the next run's
pre-flight lists it as an orphan with the run that likely wrote it.

Nothing is overwritten and **nothing is ever deleted**. There is no code path in this application
that deletes a file. The guarantee the write ordering exists to provide is narrower than
disposal: never a completion record pointing at a file that does not exist.

---

## The project directory

Everything the workflow produces is Markdown you can open without the application running.

```
project/
  artifacts/<artifact-id>/[<target>/]v<n>.md    versioned artifacts, each with a full origin header
  records/<phase>/<task>/<step>/<name>/         one directory per step, mirroring step ids
    [<target>/]                                   one level deeper per iteration item
      completion.<run-id>.s<seq>.md
      invalidation.<run-id>.s<seq>.md
      question.v<n>.md / answer.v<n>.md
      decision.<run-id>.s<seq>.md
      llm-request / llm-attempt / llm-response .md
  runs/<run-id>.md                              the run log; <run-id>.outcome.md how it ended
  state/project-state.md                        a projection, rebuilt every startup, never an input
  notes/                                        yours; never read, never validated
```

**Anything else in this tree halts the run.** Every file must be a `.md` file the workflow itself
produced, except for a small checked-in allowlist of directories it does not own (`.git`, `.hg`,
`.svn`, `.vs`, `.vscode`, `.idea`) and the operator-note convention: anything under `notes/`, and
any file named `*.note.md`. There is no per-run exception mechanism; changing the allowlist is a
code change. Every unrecognized file found is reported at once, not just the first.

An empty project directory is a valid startup state. The workflow's own earliest steps create the
project's starting files.

---

## Editing things by hand

This is expected, not tolerated.

| You want to | Do this |
| --- | --- |
| Change an artifact | Edit the `.md`. Next run reports a hand-edit, takes your content as truth, invalidates downstream. |
| Redo a step | Delete its `completion.*.md` file. Missing record means not-done; the cascade follows. Or use the console's invalidate button, which does the same thing *and* leaves a recorded cause. |
| Answer a question | Click it in the browser, or write the `answer.v<n>.md` file yourself. The question file contains the exact template. Both channels produce the same record; only the recorded surface marker differs. |
| Change an answer | You cannot, by writing a different one — that is a conflict and a halt. Invalidate the decision first; the question then opens as a new round, and the superseded records stay on disk. |
| Change a prompt | Edit the template. Its hash is a declared input of the step that uses it, so the step invalidates. |

A *malformed* record is a halt, not something skipped past.

---

## What the model is, and is not

- No tool calling, no function calling, no agent loop, no model-directed control flow.
- Not a chat client. You never converse with it; every call is a template the workflow selected.
- Model output may be an *input* to a coded predicate. It is never the predicate.

Enforced, not merely intended:

- Outgoing requests are asserted at runtime to contain no `tools`, `functions`, `tool_choice`, or
  `function_call`, and `n: 1`. A response containing `tool_calls` is a hard halt with the raw body
  preserved — a violated invariant, not an unsupported feature.
- Response acceptance is a closed whitelist: exactly `finish_reason: stop` with non-empty content.
  `length` is a halt, not a truncated artifact. Refusal, content filter, empty content, multiple
  choices, or an absent choices array are halts. Nothing is ever salvaged.
- Every call parameter — model, temperature, top_p, max_tokens, seed, timeout, parser — comes from
  the template's YAML front matter, which is the complete and only source. A missing key is a
  halt. An *unknown* key is also a halt, because that catches typos before they become defaults.
- Retries exist only for transport failure (connection reset, timeout, 429, 5xx) and are
  byte-identical resends. A parse failure or unsatisfying content is a halt with the raw response
  on disk. There is deliberately no reparse-or-reprompt loop: a repair loop is the model
  influencing what runs next.
- Every attempt is recorded and streamed, not just the successful one.
- The request record is written **before** the request is sent, so a hard-kill mid-call leaves a
  visible "initiated, never completed" record with the exact payload. The next run reports it.

---

## Prompt templates

`src/SpecRunner/prompts/*.md`, YAML front matter over a template body. Templates live with the
code because a step names one as a compiled constant and its hash is one of that step's declared
inputs — they are a dependency of a class, not project data.

```yaml
---
model: gpt-4o-mini
temperature: 0.0
top_p: 1.0
max_tokens: 2000
seed: 20260805
timeout_seconds: 120
parser: numbered-list
output_variables:
  - items
  - count
---
```

All eight keys are required and no others are permitted.

Placeholders are `{{ var_name }}`, with optional interior whitespace. A backslash immediately
before the opening delimiter suppresses substitution and is consumed: `\{{` produces a literal
`{{`. A literal backslash before a real placeholder is `\\{{var_name}}`. A stray `}}` on its own
is just text — the parser only ever looks for a matched pair.

Substitution is strict in both directions: an unresolved placeholder is a halt, a supplied
variable the template never uses is a halt, and a variable resolving to empty or whitespace is a
halt. Silence in any of those cases produces a plausible-looking prompt that is wrong.

Parsers are selected by the `parser` declaration and never by inspecting the response. Available:
`whole-markdown`, `numbered-list`, `verdict`. Each has a strict grammar; failure is a halt.

---

## Upstream precedence

A defect found downstream is treated as evidence of a defect upstream until proven otherwise.

A validator emits a `verdict:` field from a closed enum; code branches on that enum and never on
the prose beside it. A suspected upstream defect produces a **defect-finding artifact and a
block**. Backward flow does not begin until a person confirms — the application never rewinds
unattended.

Confirming runs the same invalidation cascade as any other invalidation. There is no separate
rewind engine, and no way to patch a downstream result while leaving its origin unexamined.

If the same downstream validator invalidates the same upstream target more than three times, the
run halts and presents the full revision history. An upstream/downstream pair that cannot converge
is a judgment this application does not make. The threshold is a constant in code, not a setting.

---

## The console

- The complete ordered step list, before anything runs, each row marked `skip` / `execute` /
  `not-applicable` with the reason, and a count of steps that will call the model. **Execution
  begins on an explicit start.**
- A persistent indicator of the one thing in flight: step id, iteration target, elapsed time, and
  the input hashes it is consuming.
- Click any step for its detail: declared reads and writes, the template, the resolved target, the
  downstream closure, and — for a skipped step — the record that justified skipping.
- "Explain this artifact": the full origin chain walked to roots, flagging any parent whose hash
  no longer holds. The same walk is possible by hand with a text editor; this is the convenience.
- Invalidate a step and everything downstream, with a recorded cause.
- Stop, which is read at the next step boundary. The in-flight step runs to its commit first.
- Rebuild the state projection, for after you have edited files mid-session.

Refreshing mid-run replays the whole run, not just what happens next.

---

## Layout

```
src/SpecRunner/
  Core/         canonicalization, hashing, atomic writes, the Markdown+front-matter format
  Surfaces/     the emit API, the terminal, the browser broker, the run log
  Records/      completion / invalidation / question / answer / decision records; artifacts
  Graph/        the static dependency graph and its startup validation
  Reconcile/    the project scan, the invalidation engine, the state projection, the plan
  Llm/          template loading and substitution, output parsers, the HTTP client
  Workflow/     the step model, the step context, and the methodology itself
  Engine/       the sequential runner and the operator's controls
  Web/          the console's endpoints, its page, and the provenance walker
  prompts/      the prompt templates, versioned with the code that names them
tests/          executable checks for substitution, canonicalization, and the parsers
project/        created on first run
```

```bash
dotnet run --project tests/SpecRunner.Checks
```

### The methodology is a placeholder

`Workflow/Methodology.cs` and `Workflow/MethodologySteps.cs` currently hold a small skeleton whose
purpose is to prove the engine runs, and to exercise each mechanism once — a model call, a frozen
iteration set, a human decision, a verdict branch, a defect finding, and backward flow — so that
none of that machinery sits untested behind a workflow that never reaches it.

Replacing it means rewriting those two files. Nothing else knows what the steps are: the graph is
built from their declarations alone, at startup, without executing anything. Step ids are explicit
string constants, so renaming one is a deliberate act that invalidates that step and its
descendants.

[AUTHORING.md](AUTHORING.md) walks through doing exactly that.
