// Read-only checks against a disposable local preview. The only POST is login.
// Requires Playwright/Chromium from the test environment; no production package.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const base = new URL(process.env.PTG_BROWSER_BASE_URL || 'http://127.0.0.1:5001');
if (!['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname)) {
  throw new Error('This check requires a local test preview');
}
const envPath = process.env.PTG_BROWSER_ENV_FILE;
if (!envPath) throw new Error('PTG_BROWSER_ENV_FILE must point to the private local preview environment file');
const settings = Object.fromEntries(fs.readFileSync(envPath, 'utf8').split('\n').filter(line => line && !line.startsWith('#')).map(line => {
  const equals = line.indexOf('=');
  return [line.slice(0, equals), line.slice(equals + 1)];
}));
const output = process.env.PTG_BROWSER_ARTIFACTS || '/tmp/mashal-integrated-browser';
fs.mkdirSync(output, { recursive: true });

let browser;
(async () => {
  browser = await chromium.launch({ executablePath: process.env.PTG_CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const scriptErrors = [];
  page.on('pageerror', error => scriptErrors.push(error.message));
  const login = await page.goto(new URL('/Auth/Login', base).href, { waitUntil: 'networkidle' });
  if (!login.ok()) throw new Error('Local login page failed');
  await page.locator('[name="Username"]').fill(settings.PTG_BOOTSTRAP_ADMIN_USERNAME);
  await page.locator('[name="Password"]').fill(settings.PTG_BOOTSTRAP_ADMIN_PASSWORD);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL(url => !url.pathname.startsWith('/Auth/'), { timeout: 30000 });
  await page.waitForLoadState('networkidle');

  let contractId = process.env.PTG_JOURNEY_TEST_CONTRACT_ID;
  if (!contractId) {
    await page.goto(new URL('/Contracts', base).href, { waitUntil: 'networkidle' });
    const href = await page.locator('a[href*="/ContractJourney/Details"][href*="contractId="]').first().getAttribute('href');
    if (!href) throw new Error('Disposable preview needs a seeded operational contract');
    contractId = new URL(href, base).searchParams.get('contractId');
  }
  const journey = new URL('/ContractJourney/Details', base);
  journey.searchParams.set('contractId', contractId);
  journey.searchParams.set('tab', 'summary');
  journey.searchParams.set('lockContract', 'true');
  const checks = [];
  for (const width of [1440, 1024, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    const response = await page.goto(journey.href, { waitUntil: 'networkidle' });
    if (!response.ok() || page.url().includes('/Auth/')) throw new Error('Journey GET failed');
    await page.locator('[data-contract-journey-page]').waitFor();
    const layout = await page.evaluate(() => ({
      viewport: innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      direction: getComputedStyle(document.documentElement).direction,
      links: document.querySelectorAll('[data-contract-journey-tab-nav] [data-contract-journey-tab-link]').length,
      groups: document.querySelectorAll('[data-navigation-group]').length,
      avatars: Array.from(document.querySelectorAll('.ak-cycle-avatar')).map(avatar => ({ width: avatar.getBoundingClientRect().width, height: avatar.getBoundingClientRect().height }))
    }));
    if (layout.direction !== 'rtl') throw new Error('Actual Dari Journey must render RTL');
    if (layout.documentWidth > layout.viewport + 1) throw new Error(`Journey document overflows at ${width}: ${layout.documentWidth}`);
    if (layout.groups !== 3 || ![3, 7].includes(layout.links)) throw new Error('Expected existing sale/purchase routes grouped in three areas');
    if (!layout.avatars.length || layout.avatars.some(a => a.width <= 0 || a.height <= 0)) throw new Error('Cycle artwork has no stable display dimensions');
    await page.screenshot({ path: path.join(output, `journey-actual-${width}.png`), fullPage: true });
    const sales = page.locator('[data-contract-journey-tab-nav] [data-contract-journey-tab="sales"]');
    await sales.focus();
    await sales.press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-contract-journey-tab-content]').getAttribute('data-active-tab') === 'sales');
    const focused = await page.evaluate(() => document.activeElement.getAttribute('data-contract-journey-tab'));
    if (focused !== 'sales') throw new Error('Actual tab update lost keyboard focus');
    checks.push({ width, ...layout, focused });
  }
  const pages = [];
  await page.setViewportSize({ width: 390, height: 1000 });
  for (const pathname of ['/Contracts', '/Loading', '/Sales/CreateGroup', '/Expenses/CreateGroup']) {
    const response = await page.goto(new URL(pathname, base).href, { waitUntil: 'networkidle' });
    if (!response.ok() || page.url().includes('/Auth/')) throw new Error(`Actual page failed: ${pathname}`);
    const dimensions = await page.evaluate(() => ({ viewport: innerWidth, documentWidth: document.documentElement.scrollWidth }));
    if (dimensions.documentWidth > dimensions.viewport + 1) throw new Error(`Document overflow: ${pathname}`);
    await page.screenshot({ path: path.join(output, `${pathname.replaceAll('/', '-').slice(1)}-390.png`), fullPage: true });
    pages.push({ pathname, status: response.status(), ...dimensions });
  }
  const result = { databaseBacked: true, checks, pages, scriptErrors, businessWrites: 0 };
  fs.writeFileSync(path.join(output, 'integrated-browser-checks.json'), JSON.stringify(result, null, 2));
  await browser.close();
  console.log(JSON.stringify(result));
  if (scriptErrors.length) process.exitCode = 1;
})().catch(async error => {
  if (browser) await browser.close();
  console.error(error.message);
  process.exitCode = 1;
});
