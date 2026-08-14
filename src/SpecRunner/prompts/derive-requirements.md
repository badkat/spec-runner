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

You are transforming a project brief into a flat list of requirements.

Read the brief below and produce the requirements it implies.

Output format — this is a strict grammar and anything else will be rejected:

- Every line is `N. <requirement>`, numbered contiguously starting at 1.
- One requirement per line. No blank lines inside the list, no headings, no preamble,
  no closing remarks, no bullet characters, no bold.
- Each requirement is a single sentence stating one testable obligation of the system.

## Project brief

{{ project_brief }}
