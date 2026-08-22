---
description: Read the element picked in the browser and locate/analyse/fix it in the project
argument-hint: [what to change about the picked element]
allowed-tools: Read, Grep, Glob, Edit, Bash, PowerShell, Skill
---

# UI Pick — from browser selection to a targeted code change

User request for the picked element: **$ARGUMENTS**

## 1. Read the selection — always, without being asked

Read `.ptg-ui-pick/last-pick.md` first (short digest). Read
`.ptg-ui-pick/last-pick.json` whenever you need `attributes`, `computedStyles`,
full `outerHTML`, `ancestors`, or `children` — which is most of the time for a
visual change.

`$ARGUMENTS` may already carry an element summary (the VS Code bridge prefills
it). Ignore that summary as a source of truth and use the files — they are
complete and current. If `$ARGUMENTS` also contains a request in prose, that is
the change to make.

Check `pickedAt`: if it is more than ~30 minutes old, say so in one line before
proceeding, in case the user meant a newer selection.

If neither file exists, tell the user to run the `PTG: UI Dev (app + pick
server)` task and press `Alt+Shift+P` in the browser, then stop.

## 2. Locate the source — in this order

1. `source.view` / `source.viewChain` — the Razor file(s) that rendered the
   element. Trust this first. A chain entry like `Partials/_PageLoader` is a
   partial name: resolve it under `Views/Shared/` (or the controller's `Views/`
   folder). The page-level entry is a full path like `/Views/Auth/Login.cshtml`.
   Only `<partial name="…" />` renders are marked — `Html.PartialAsync(…)` and
   `<vc:… />` view components are NOT, so if the chain contains only the
   page-level view the element may still come from a nested partial or a
   ViewComponent. In that case grep `source.classHints` across
   `Views/Shared/`, `Views/<Controller>/` and `ViewComponents/`.
2. If `source.view` is null (picker ran without `PTG_UI_PICK=1`), derive the view
   from `page.url` → controller/action → `src/PTGOilSystem.Web/Views/<Controller>/<Action>.cshtml`.
3. Confirm inside that file with the strongest available anchor, in order:
   `element.id` → `element.dataset` keys → `source.classHints` →
   the literal `element.text`.
4. CSS: grep `source.classHints` in `src/PTGOilSystem.Web/wwwroot/css/` and in
   `design-system/`.
5. JS: grep `source.classHints`, `element.id` and `element.dataset` keys in
   `src/PTGOilSystem.Web/wwwroot/js/`. `source.pageAssets` lists the JS files the
   page actually loaded — check those first.
6. Controller / ViewModel: only if the change needs data that is not already in
   the view. `page.controller`/`page.action` or the URL gives the controller;
   the view's `@model` gives the ViewModel.

Use the `graphify` skill for structural questions before raw grep.

## 3. Analyse before editing

- Read the identified view/CSS/JS regions fully before changing anything.
- State in one or two lines which file+region owns the element and why.
- If the element comes from a shared partial (`Views/Shared/_*.cshtml`), say so —
  a change there hits every page that uses it. Ask before editing a shared
  partial unless the user explicitly asked for a global change.

## 4. Check the design system before choosing a visual solution

Mandatory for any visual change — the point is to reuse what exists, not invent:

1. Load the `ptg-ui-design-rules` skill; follow its source order and reading order.
2. Compare the pick's `computedStyles` against the project's tokens and existing
   component classes in `design-system/` and `src/PTGOilSystem.Web/wwwroot/css/`.
3. Prefer an existing class/token over a new rule. Only add CSS when nothing
   in the design system covers it, and put it where that file's conventions say.
4. Keep RTL, Persian/Dari typography, print and responsive behaviour intact.

## 5. Change

- Smallest safe edit, only the picked region. No refactors, no renames, no
  unrelated cleanup, no new inline styles, no new `!important`.
- Do not touch Controller / Model / Entity / Migration / DbContext / business
  logic — stock, inventory, ledger, payments, sales, FX, P&L. If the request
  genuinely cannot be done without one of those, stop and say so first.

## 6. Verify

- `dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj` when C#/Razor changed.
- Targeted tests per the `ptg-build-test-minimal` skill.
- Playwright MCP re-check when it is connected and the app is running:
  `browser_navigate` to `page.url` → `browser_evaluate` on `element.cssPath` to
  read back the changed properties → `browser_take_screenshot` of that element.
  Report what you actually observed. If the app is not running or login blocks
  the page, say that instead of claiming a visual check happened.

## 7. Report

Report in this shape, short:

```text
Element : <tag>#<id>.<classes>
Source  : <file:line>
CSS/JS  : <files>
Design  : <token/class reused, or why a new rule was needed>
Change  : <one line>
Build   : <ok/fail>
Verify  : <what Playwright showed, or how the user checks it in the browser>
```
