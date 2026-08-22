/*
 * PTG UI Pick Bridge — browser selection → Claude Code panel.
 *
 * Watches .ptg-ui-pick/last-pick.json (written by tools/ui-pick/server.mjs) and
 * delivers the picked element to Claude Code using only documented VS Code API
 * and the commands the official Claude Code extension registers:
 *
 *   claude-vscode.focus                     focus the input of the open session
 *   claude-vscode.editor.open(id, prompt)   open a NEW Claude tab, prompt prefilled
 *
 * There is no official API for writing text into an ALREADY OPEN Claude Code
 * session; the extension itself refuses that ("Session is already open. Your
 * prompt was not applied"). So the closest supported flows are the two above,
 * plus the clipboard. No UI automation, no keystroke simulation, no poking at
 * Claude Code internals.
 */

const vscode = require('vscode');
const fs = require('fs');
const path = require('path');

const CLAUDE_EXTENSION_ID = 'anthropic.claude-code';
const PICK_DIR = '.ptg-ui-pick';
const PICK_FILE = 'last-pick.json';
const DIGEST_FILE = 'last-pick.md';

let output;
let statusBar;
let lastPromptText = '';
let lastPickedAt = '';
let debounceTimer;

function config() {
  return vscode.workspace.getConfiguration('ptgUiPick');
}

function workspaceRoot() {
  const folders = vscode.workspace.workspaceFolders;
  return folders && folders.length ? folders[0].uri.fsPath : null;
}

function pickPaths() {
  const root = workspaceRoot();
  if (!root) return null;
  return {
    json: path.join(root, PICK_DIR, PICK_FILE),
    digest: path.join(root, PICK_DIR, DIGEST_FILE)
  };
}

function readPick() {
  const paths = pickPaths();
  if (!paths || !fs.existsSync(paths.json)) return null;
  try {
    const pick = JSON.parse(fs.readFileSync(paths.json, 'utf8'));
    pick.__digest = fs.existsSync(paths.digest) ? fs.readFileSync(paths.digest, 'utf8') : null;
    return pick;
  } catch (err) {
    output.appendLine(`[ui-pick] cannot read ${paths.json}: ${err.message}`);
    return null;
  }
}

function describe(pick) {
  const el = (pick && pick.element) || {};
  let s = el.tag || 'element';
  if (el.id) s += `#${el.id}`;
  if (el.classes && el.classes.length) s += `.${el.classes.slice(0, 3).join('.')}`;
  return s;
}

function buildPrompt(pick) {
  const el = pick.element || {};
  const src = pick.source || {};
  const page = pick.page || {};
  const head =
    `/ui-pick ${describe(pick)} — view: ${src.view || '?'} — ${page.url || '?'} — `;

  if (config().get('promptStyle') !== 'full') {
    // Claude reads .ptg-ui-pick/last-pick.md itself; keep the input line short
    // and leave the cursor where the user types the actual request.
    return head;
  }

  const lines = [
    head.trimEnd(),
    '',
    `URL: ${page.url || '?'}`,
    `Route: ${page.controller || '?'} / ${page.action || '?'}`,
    `View: ${src.view || '?'}`,
    `View chain: ${(src.viewChain || []).join(' -> ') || '(none)'}`,
    `Selector: ${el.cssPath || '?'}`,
    `Tag: ${el.tag || '?'}${el.id ? ' #' + el.id : ''}`,
    `Classes: ${(el.classes || []).join(' ') || '(none)'}`,
    `Class hints: ${(src.classHints || []).join(' ') || '(none)'}`,
    `Text: ${el.text || '(empty)'}`,
    `Rect: ${el.rect ? `${el.rect.width}x${el.rect.height} @ ${el.rect.x},${el.rect.y}` : '?'}`,
    '',
    'outerHTML:',
    el.outerHTML || '',
    '',
    'Computed styles and the full payload: .ptg-ui-pick/last-pick.json'
  ];
  return lines.join('\n');
}

function claudeAvailable() {
  return !!vscode.extensions.getExtension(CLAUDE_EXTENSION_ID);
}

async function deliver(pick, { silent } = {}) {
  const prompt = buildPrompt(pick);
  lastPromptText = prompt;
  lastPickedAt = pick.pickedAt || '';

  await vscode.env.clipboard.writeText(prompt);

  const mode = config().get('deliveryMode');
  const label = describe(pick);

  if (!claudeAvailable()) {
    vscode.window.showWarningMessage(
      'Claude Code extension not found. The UI pick was copied to the clipboard only.'
    );
    updateStatusBar(pick);
    return;
  }

  try {
    if (mode === 'newClaudeTab') {
      // sessionId undefined -> new conversation, prompt prefilled in the input.
      await vscode.commands.executeCommand('claude-vscode.editor.open', undefined, prompt);
      if (!silent) {
        vscode.window.setStatusBarMessage(`$(comment-discussion) UI Pick: ${label} — press Enter in Claude`, 6000);
      }
    } else if (mode === 'clipboardOnly') {
      if (!silent) {
        vscode.window.setStatusBarMessage(`$(clippy) UI Pick copied: ${label}`, 6000);
      }
    } else {
      await vscode.commands.executeCommand('claude-vscode.focus');
      if (!silent) {
        vscode.window.setStatusBarMessage(`$(clippy) UI Pick: ${label} — press Ctrl+V then Enter`, 6000);
      }
    }
  } catch (err) {
    output.appendLine(`[ui-pick] delivery failed (${mode}): ${err.message}`);
    vscode.window.showWarningMessage(
      `Could not hand the UI pick to Claude Code (${err.message}). It is on the clipboard.`
    );
  }

  updateStatusBar(pick);
  output.appendLine(`[ui-pick] delivered ${label} (${mode}) at ${lastPickedAt}`);
}

function updateStatusBar(pick) {
  if (!pick) {
    statusBar.hide();
    return;
  }
  statusBar.text = `$(inspect) ${describe(pick)}`;
  statusBar.tooltip =
    `UI Pick — ${(pick.source && pick.source.view) || 'unknown view'}\n` +
    `${(pick.page && pick.page.url) || ''}\n` +
    'Click or press Alt+Shift+C to send it to Claude Code.';
  statusBar.show();
  vscode.commands.executeCommand('setContext', 'ptgUiPick.hasPick', true);
}

function onPickFileChanged() {
  clearTimeout(debounceTimer);
  debounceTimer = setTimeout(() => {
    const pick = readPick();
    if (!pick) return;
    if (pick.pickedAt && pick.pickedAt === lastPickedAt) return; // same pick re-saved
    if (config().get('autoDeliver')) {
      deliver(pick);
    } else {
      lastPickedAt = pick.pickedAt || '';
      updateStatusBar(pick);
    }
  }, 150);
}

function activate(context) {
  output = vscode.window.createOutputChannel('PTG UI Pick');
  context.subscriptions.push(output);

  statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 90);
  statusBar.command = 'ptgUiPick.sendLastPickToClaude';
  context.subscriptions.push(statusBar);

  const root = workspaceRoot();
  if (!root) {
    output.appendLine('[ui-pick] no workspace folder — bridge idle.');
    return;
  }

  const pattern = new vscode.RelativePattern(root, `${PICK_DIR}/${PICK_FILE}`);
  const watcher = vscode.workspace.createFileSystemWatcher(pattern);
  watcher.onDidCreate(onPickFileChanged, null, context.subscriptions);
  watcher.onDidChange(onPickFileChanged, null, context.subscriptions);
  context.subscriptions.push(watcher);

  context.subscriptions.push(
    vscode.commands.registerCommand('ptgUiPick.sendLastPickToClaude', async () => {
      const pick = readPick();
      if (!pick) {
        vscode.window.showInformationMessage(
          'No UI pick yet. Start the pick server, then press Alt+Shift+P in the browser and click an element.'
        );
        return;
      }
      lastPickedAt = ''; // force delivery even if this pick was already delivered
      await deliver(pick);
    }),

    vscode.commands.registerCommand('ptgUiPick.copyLastPick', async () => {
      const pick = readPick();
      if (!pick) return;
      const text = pick.__digest || buildPrompt(pick);
      await vscode.env.clipboard.writeText(text);
      vscode.window.setStatusBarMessage('$(clippy) UI Pick copied', 4000);
    }),

    vscode.commands.registerCommand('ptgUiPick.openLastPickFile', async () => {
      const paths = pickPaths();
      if (!paths || !fs.existsSync(paths.json)) return;
      const doc = await vscode.workspace.openTextDocument(
        fs.existsSync(paths.digest) ? paths.digest : paths.json
      );
      await vscode.window.showTextDocument(doc, { preview: true });
    }),

    vscode.commands.registerCommand('ptgUiPick.toggleAutoDeliver', async () => {
      const next = !config().get('autoDeliver');
      await config().update('autoDeliver', next, vscode.ConfigurationTarget.Workspace);
      vscode.window.showInformationMessage(
        `PTG UI Pick: automatic delivery ${next ? 'enabled' : 'disabled'}.`
      );
    })
  );

  const existing = readPick();
  if (existing) {
    lastPickedAt = existing.pickedAt || '';
    updateStatusBar(existing);
  }

  output.appendLine(
    `[ui-pick] bridge active. watching ${path.join(root, PICK_DIR, PICK_FILE)} — ` +
      `Claude Code extension ${claudeAvailable() ? 'found' : 'NOT found'}.`
  );
}

function deactivate() {}

module.exports = { activate, deactivate };
