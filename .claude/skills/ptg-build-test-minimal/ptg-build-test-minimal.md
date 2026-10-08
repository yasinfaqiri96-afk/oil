---
name: ptg-build-test-minimal
description: Use after PTG code changes to run only the minimum useful build and tests.
effort: low
---

Run minimal verification.

Rules:
- Do not run all tests unless necessary.
- Batch edits and verify once with `scripts/dev-verify.ps1`; follow AGENTS.md's scope rules.
- CSS/JS/text only: `-Ui -Paths <task-files>`; Razor keeps one compilation check.
- C# with tests: `-Web -Filter 'FullyQualifiedName~RelatedClass'` builds the test graph once (including Web), then runs only related tests with `--no-build --no-restore`. Do not separately build Web first.
- Without tests: `-Web` builds only Web. Repeat tests against a fresh test DLL with `--no-build --no-restore`.
- No Graphify, implicit restore, full solution build, or full suite for ordinary fixes.
- If full test is needed, explain why first.
- Do not run slow scans unless requested.
- Report only:
  build status,
  test status,
  errors if any,
  next safe action.
