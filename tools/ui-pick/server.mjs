// PTG UI Pick — local bridge between the browser element picker and Claude Code.
//
// The picker overlay (wwwroot/js/dev/ptg-ui-pick.js) POSTs the selected element
// here; this server writes it into .ptg-ui-pick/ inside the repository so that
// Claude Code can read the selection as a normal workspace file.
//
// Dev-only tool. Binds to loopback, accepts a fixed origin allowlist, no deps.
//
//   node tools/ui-pick/server.mjs
//
// Env:
//   PTG_UI_PICK_PORT     listen port (default 5199)
//   PTG_UI_PICK_ORIGIN   extra allowed origin, e.g. http://localhost:5050

import { createServer } from 'node:http';
import { mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync, unlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(HERE, '..', '..');
const OUT_DIR = join(REPO_ROOT, '.ptg-ui-pick');
const HISTORY_DIR = join(OUT_DIR, 'history');
const LAST_JSON = join(OUT_DIR, 'last-pick.json');
const LAST_MD = join(OUT_DIR, 'last-pick.md');

const PORT = Number(process.env.PTG_UI_PICK_PORT || 5199);
const HOST = '127.0.0.1';
const MAX_BODY = 2 * 1024 * 1024; // 2 MB
const HISTORY_KEEP = 50;

const ALLOWED_ORIGINS = new Set(
  [
    'http://localhost:5000',
    'http://127.0.0.1:5000',
    'https://localhost:5001',
    'https://127.0.0.1:5001',
    process.env.PTG_UI_PICK_ORIGIN,
  ].filter(Boolean)
);

mkdirSync(HISTORY_DIR, { recursive: true });

function cors(req, res) {
  const origin = req.headers.origin;
  if (origin && ALLOWED_ORIGINS.has(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Vary', 'Origin');
    res.setHeader('Access-Control-Allow-Methods', 'POST, GET, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
    res.setHeader('Access-Control-Max-Age', '600');
    return true;
  }
  return !origin; // same-process / curl calls with no Origin header
}

function json(res, code, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (c) => {
      size += c.length;
      if (size > MAX_BODY) {
        reject(new Error('payload too large'));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function trimHistory() {
  const files = readdirSync(HISTORY_DIR)
    .filter((f) => f.endsWith('.json'))
    .sort();
  while (files.length > HISTORY_KEEP) {
    const victim = files.shift();
    try {
      unlinkSync(join(HISTORY_DIR, victim));
    } catch {
      /* best effort */
    }
  }
}

function line(label, value) {
  if (value === undefined || value === null || value === '' ) return '';
  return `- **${label}:** ${value}\n`;
}

/** Human/agent readable digest so Claude Code can read one short file first. */
function toMarkdown(p) {
  const el = p.element || {};
  const src = p.source || {};
  let md = `# PTG UI Pick\n\n`;
  md += line('Picked at', p.pickedAt);
  md += line('URL', p.page?.url);
  md += line('Controller/Action', p.page?.controller && `${p.page.controller} / ${p.page.action}`);
  md += line('Title', p.page?.title);
  md += `\n## Element\n\n`;
  md += line('Tag', `\`${el.tag}\``);
  md += line('id', el.id && `\`${el.id}\``);
  md += line('Classes', el.classes?.length && el.classes.map((c) => `\`${c}\``).join(' '));
  md += line('Selector', el.cssPath && `\`${el.cssPath}\``);
  md += line('Text', el.text && `«${el.text}»`);
  if (el.dataset && Object.keys(el.dataset).length) {
    md += line(
      'data-*',
      Object.entries(el.dataset)
        .map(([k, v]) => `\`data-${k}="${v}"\``)
        .join(' ')
    );
  }
  md += line('Rect', el.rect && `x=${el.rect.x} y=${el.rect.y} w=${el.rect.width} h=${el.rect.height}`);

  md += `\n## Likely source\n\n`;
  md += line('Razor view (data-ptg-view)', src.view && `\`${src.view}\``);
  if (src.viewChain?.length) {
    md += line('View chain (inner → outer)', src.viewChain.map((v) => `\`${v}\``).join(' → '));
  }
  if (src.pageAssets?.length) {
    md += line('Page JS assets on this page', src.pageAssets.map((a) => `\`${a}\``).join(', '));
  }
  if (src.classHints?.length) {
    md += line('Distinct class hints to grep', src.classHints.map((c) => `\`${c}\``).join(', '));
  }

  if (p.ancestors?.length) {
    md += `\n## Ancestors (closest first)\n\n`;
    for (const a of p.ancestors) {
      md += `- \`${a.tag}\`${a.id ? `#${a.id}` : ''}${a.classes ? `.${a.classes.join('.')}` : ''}${a.view ? ` — view: \`${a.view}\`` : ''}\n`;
    }
  }

  if (p.children?.length) {
    md += `\n## Direct children\n\n`;
    for (const c of p.children) {
      md += `- \`${c.tag}\`${c.id ? `#${c.id}` : ''}${c.classes ? `.${c.classes.join('.')}` : ''}${c.text ? ` — «${c.text}»` : ''}\n`;
    }
  }

  if (p.computedStyles && Object.keys(p.computedStyles).length) {
    md += `\n## Computed styles (subset)\n\n\`\`\`css\n`;
    for (const [k, v] of Object.entries(p.computedStyles)) md += `${k}: ${v};\n`;
    md += '```\n';
  }

  if (el.outerHTML) {
    md += `\n## outerHTML (truncated)\n\n\`\`\`html\n${el.outerHTML}\n\`\`\`\n`;
  }

  if (p.note) {
    md += `\n## User note\n\n${p.note}\n`;
  }

  md += `\nFull payload: \`.ptg-ui-pick/last-pick.json\`\n`;
  return md;
}

const server = createServer(async (req, res) => {
  const ok = cors(req, res);
  const url = new URL(req.url, `http://${HOST}:${PORT}`);

  if (req.method === 'OPTIONS') {
    res.writeHead(ok ? 204 : 403).end();
    return;
  }

  if (!ok) {
    json(res, 403, { error: 'origin not allowed' });
    return;
  }

  if (req.method === 'GET' && url.pathname === '/health') {
    json(res, 200, { ok: true, out: OUT_DIR, port: PORT });
    return;
  }

  if (req.method === 'GET' && url.pathname === '/last') {
    if (!existsSync(LAST_JSON)) {
      json(res, 404, { error: 'no pick yet' });
      return;
    }
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(readFileSync(LAST_JSON));
    return;
  }

  if (req.method === 'POST' && url.pathname === '/pick') {
    let payload;
    try {
      payload = JSON.parse(await readBody(req));
    } catch (err) {
      json(res, 400, { error: `bad payload: ${err.message}` });
      return;
    }

    payload.pickedAt = new Date().toISOString();
    const stamp = payload.pickedAt.replace(/[:.]/g, '-');
    const text = JSON.stringify(payload, null, 2);

    writeFileSync(LAST_JSON, text, 'utf8');
    writeFileSync(join(HISTORY_DIR, `pick-${stamp}.json`), text, 'utf8');
    writeFileSync(LAST_MD, toMarkdown(payload), 'utf8');
    trimHistory();

    const el = payload.element || {};
    console.log(
      `[ui-pick] ${payload.pickedAt}  <${el.tag}>` +
        `${el.id ? '#' + el.id : ''}${el.classes?.length ? '.' + el.classes.join('.') : ''}` +
        `  view=${payload.source?.view || '?'}  url=${payload.page?.url || '?'}`
    );

    json(res, 200, { ok: true, file: '.ptg-ui-pick/last-pick.json' });
    return;
  }

  json(res, 404, { error: 'not found' });
});

server.listen(PORT, HOST, () => {
  console.log(`[ui-pick] listening on http://${HOST}:${PORT}`);
  console.log(`[ui-pick] writing picks to ${OUT_DIR}`);
  console.log(`[ui-pick] allowed origins: ${[...ALLOWED_ORIGINS].join(', ')}`);
  console.log('[ui-pick] in the browser press Alt+Shift+P, then click an element.');
});

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`[ui-pick] port ${PORT} is already in use — is another pick server running?`);
    process.exit(1);
  }
  throw err;
});
