---
model: gpt-4o-mini
temperature: 0.0
top_p: 1.0
max_tokens: 1500
seed: 20260805
timeout_seconds: 180
parser: verdict
output_variables:
  - verdict
  - suspected_artifact
  - rationale
---

You are checking a set of specifications against the requirement list they were written from.

Decide one thing only: whether the specifications can be written soundly from these
requirements, or whether the requirement list itself is defective — ambiguous, contradictory,
or missing something the specifications had to invent.

A specification being merely thin is not an upstream defect. Suspect the requirements only when
the specification could not have been written correctly from what the requirements say.

Reply with a YAML front matter block followed by prose. Exactly this shape:

\{{ nothing here is substituted — the literal braces below are part of your output format }}

---
verdict: pass
suspected_artifact: "-"
---

Your reasoning, in prose, for a human reader.

Rules for the front matter:

- `verdict` is exactly `pass` or `upstream-defect-suspected`. No other value is accepted.
- `suspected_artifact` is `requirements` when the verdict is `upstream-defect-suspected`,
  and `"-"` when the verdict is `pass`. No other value is accepted.
- The two keys above are the only keys. The prose beneath the block is for the human and is
  never read by the code that decides what happens next.

## The requirements

{{ requirements }}

## The specifications written from them

{{ specifications }}
