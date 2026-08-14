---
model: gpt-4o-mini
temperature: 0.0
top_p: 1.0
max_tokens: 2000
seed: 20260805
timeout_seconds: 180
parser: whole-markdown
output_variables:
  - content
---

You are writing the specification for exactly one requirement.

Write Markdown only. Do not restate the brief, do not speculate about other requirements,
and do not add front matter — the application adds the origin header itself.

Use these headings, in this order, and no others:

## Statement
## Behaviour
## Acceptance criteria
## Out of scope

## The requirement

{{ requirement }}

## The project brief it came from

{{ project_brief }}
