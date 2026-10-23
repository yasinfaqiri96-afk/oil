import express from 'express';
import makeWASocket, { Browsers, DisconnectReason, useMultiFileAuthState } from 'baileys';
import QRCode from 'qrcode-terminal';
import P from 'pino';

const port = Number(process.env.WA_SENDER_PORT || 8787);
const secret = process.env.WA_SENDER_SECRET;
const minIntervalMs = Math.max(30000, Number(process.env.WA_MIN_INTERVAL_MS || 60000));
const maxQueue = Math.max(1, Number(process.env.WA_MAX_QUEUE || 100));
const authDir = process.env.WA_AUTH_DIR || './.baileys_auth';
if (!secret) throw new Error('WA_SENDER_SECRET is required.');

const app = express();
app.use(express.json({ limit: '32kb' }));
const queue = [];
let sock = null;
let ready = false;
let busy = false;
let lastSentAt = 0;
const logger = P({ level: process.env.WA_LOG_LEVEL || 'warn' });

function auth(req, res, next) {
  if (req.get('x-wa-sender-secret') !== secret) return res.status(401).json({ error: 'unauthorized' });
  next();
}
function normalize(raw) {
  let digits = String(raw || '').replace(/[^0-9]/g, '');
  if (digits.startsWith('00')) digits = digits.slice(2);
  if (digits.startsWith('0')) digits = `93${digits.slice(1)}`;
  if (digits.length < 8 || digits.length > 15) throw new Error('invalid recipient phone');
  return `${digits}@s.whatsapp.net`;
}
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

async function pump() {
  if (busy || !ready || !sock || queue.length === 0) return;
  busy = true;
  const item = queue.shift();
  try {
    const wait = Math.max(0, lastSentAt + minIntervalMs - Date.now());
    if (wait) await sleep(wait);
    const registered = await sock.onWhatsApp(item.to);
    if (!registered?.some(result => result.exists)) {
      throw new Error(`recipient is not registered on WhatsApp: ${item.to}`);
    }
    await sock.sendMessage(item.to, { text: item.text });
    lastSentAt = Date.now();
    console.log(JSON.stringify({ event: 'sent', id: item.id }));
  } catch (error) {
    console.error(JSON.stringify({ event: 'failed', id: item.id, error: String(error) }));
  } finally {
    busy = false;
    setImmediate(pump);
  }
}

async function connect() {
  const { state, saveCreds } = await useMultiFileAuthState(authDir);
  sock = makeWASocket({
    auth: state,
    browser: Browsers.ubuntu('Chrome'),
    printQRInTerminal: false,
    logger
  });
  sock.ev.on('creds.update', saveCreds);
  sock.ev.on('connection.update', ({ connection, lastDisconnect, qr }) => {
    if (qr) {
      ready = false;
      console.log('QR را با شمارهٔ جداگانهٔ واتساپ اسکن کنید:');
      QRCode.generate(qr, { small: true });
    }
    if (connection === 'open') {
      ready = true;
      console.log('Baileys WhatsApp sender ready.');
      pump();
    }
    if (connection === 'close') {
      ready = false;
      const status = lastDisconnect?.error?.output?.statusCode;
      console.error(`WhatsApp connection closed (${status ?? 'unknown'}).`);
      if (status !== DisconnectReason.loggedOut) setTimeout(connect, 5000);
    }
  });
}

app.get('/health', auth, (req, res) => res.json({ ready, queue: queue.length, minIntervalMs }));
app.post('/send-receipt', auth, (req, res) => {
  if (queue.length >= maxQueue) return res.status(429).json({ error: 'queue_full' });
  const id = String(req.body?.id || '').trim();
  const text = String(req.body?.text || '').trim();
  if (!id || !text || text.length > 4000) return res.status(400).json({ error: 'id_and_text_required' });
  let to;
  try { to = normalize(req.body?.phone); } catch { return res.status(400).json({ error: 'invalid_phone' }); }
  queue.push({ id, to, text, queuedAt: new Date().toISOString() });
  pump();
  res.status(202).json({ accepted: true, id, queue: queue.length, ready });
});

app.listen(port, '127.0.0.1', () => console.log(`Baileys sender listening on 127.0.0.1:${port}`));
connect().catch(error => { console.error(error); process.exitCode = 1; });
