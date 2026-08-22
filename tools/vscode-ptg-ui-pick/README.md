# PTG UI Pick Bridge

Local, unpublished VS Code extension. Watches `.ptg-ui-pick/last-pick.json` and
hands the element picked in the browser to the Claude Code panel.

## Install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ..\..\scripts\install-ui-pick-extension.ps1
```

Then reload the VS Code window. Uninstall with `-Uninstall`.

## What it uses

Only documented VS Code API plus the commands the official Claude Code
extension registers:

- `vscode.workspace.createFileSystemWatcher` — detect a new pick
- `vscode.env.clipboard.writeText` — put the prompt on the clipboard
- `claude-vscode.focus` — focus the input of the open Claude session
- `claude-vscode.editor.open(sessionId, prompt, viewColumn)` — open a NEW Claude
  tab with the prompt already typed in

There is no supported way to write text into an already-open Claude Code
session, and no way to send a message programmatically. The Claude Code
extension exposes no public API object (`module.exports = { activate,
deactivate }`) and its own code refuses a prompt for an open session
("Session is already open. Your prompt was not applied — enter it manually").
No UI automation or keystroke simulation is used.

## Commands

| Command | Default key |
|---|---|
| `PTG: Send Selected UI Element to Claude Code` | `Alt+Shift+C` |
| `PTG: Copy Last UI Pick to Clipboard` | — |
| `PTG: Open Last UI Pick File` | — |
| `PTG: Toggle Automatic UI Pick Delivery` | — |

## Settings

- `ptgUiPick.autoDeliver` (default `true`)
- `ptgUiPick.deliveryMode`: `focusAndClipboard` (default) / `newClaudeTab` / `clipboardOnly`
- `ptgUiPick.promptStyle`: `summary` (default) / `full`
