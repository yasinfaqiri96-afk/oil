# PTG Oil System

## Mandatory Rules

- Read and understand the existing code before making changes.
- Only modify files directly related to the current request.
- Do not refactor or redesign unrelated areas.
- Do not change Entity, Migration, DbContext, or database structure unless explicitly requested.
- Do not change Stock, Inventory, Ledger, Payment, Sales, FX, or P&L logic unless explicitly requested.
- Never guess business behavior.
- First identify the root cause and related files.
- Make the smallest safe change.
- Run build and relevant tests after changes.
- Keep final responses short and precise.

## UI/UX prohibitions (always apply)

- React، Vue، Tailwind، shadcn و framework جدید ممنوع است.
- Business Logic، Controller، Model، Route، permission و Database بدون ضرورت و درخواست صریح تغییر نکند.
- Inline style، CSS تکراری و `!important` جدید ممنوع است.
- تغییر خارج از scope، بازطراحی سراسری و cleanup نامرتبط ممنوع است.

For any UI/UX request, follow the `ptg-ui-design-rules` skill (source order, design-system reading order, review checklist).

## graphify

This project has a knowledge graph at `graphify-out/`. For codebase questions, use the `graphify` skill before raw grep or source browsing; after modifying code, run `graphify update .`.

## Execution efficiency

- Start with only files directly related to the requested task.
- Do not explore the entire repository unless necessary.
- Do not use subagents, broad research, or review workflows unless explicitly needed.
- Do not repeatedly run git status, git diff, build, or tests after every edit.
- For UI-only changes, inspect the View + directly related CSS/JS first.
- Run targeted tests when available.
- Run one final build after implementation when code compilation may be affected.
- Run the full test suite only for cross-cutting, accounting, inventory, migration, or explicitly requested changes.
- Reuse information already discovered in the current task.
- Do not reopen unchanged files without a reason.
- Prefer direct implementation over prolonged planning for well-scoped tasks.