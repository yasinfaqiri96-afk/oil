const fs = require('fs');
const http = require('http');
const path = require('path');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../../src/PTGOilSystem.Web/wwwroot');
const output = process.env.PTG_BROWSER_ARTIFACTS || '/tmp/mashal-journey-browser';
fs.mkdirSync(output, { recursive: true });
const groups = [['overview', 'نمای کلی', [['summary', 'خلاصه']]], ['operations', 'بارها و عملیات', [['loadings', 'بارگیری و رسید'], ['inventory', 'موجودی'], ['inventorytransport', 'حمل و نقل'], ['sales', 'فروشات']]], ['finance', 'مالی و مصارف', [['costs', 'مصارف و ضایعات'], ['finance', 'حساب قرارداد']]]];
function fragment(active) {
  const navigation = groups.map(([key, label, tabs]) => `<div class="ak-navigation-group" data-navigation-group="${key}"><span class="ak-navigation-group-label">${label}</span><nav class="ptg-tabs-rail" aria-label="${label}">${tabs.map(([key, text]) => `<a class="ptg-tab-item ${key === active ? 'is-active' : ''}" href="/ContractJourney/Details?contractId=17&tab=${key}&lockContract=true" data-contract-journey-tab-link="true" data-contract-journey-tab="${key}" data-no-spa="true" ${key === active ? 'aria-current="page"' : ''}><span data-tab-label="${text}">${text}</span></a>`).join('')}</nav></div>`).join('');
  return `<main class="ak-detail-page" data-contract-journey-page="true"><h1>قرارداد آزمایشی</h1><div class="ak-navigation-groups" data-contract-journey-tab-nav="true">${navigation}</div><div data-contract-journey-tab-content="true" data-active-tab="${active}" data-loading-text="در حال بارگذاری" data-error-text="این بخش باز نشد. دوباره تلاش کنید." data-retry-text="دوباره باز کردن این بخش"><h2>${active}</h2></div></main>`;
}
const css = ['01-tokens', '02-base', '11-details', '45-akaunting', '50-ak-components', '16-system-tabs'].map(f => `<link rel="stylesheet" href="/css/ptg/${f}.css">`).join('');
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname.startsWith('/css/') || url.pathname.startsWith('/js/')) {
    res.setHeader('Content-Type', url.pathname.endsWith('.css') ? 'text/css' : 'text/javascript');
    res.end(fs.readFileSync(path.join(root, url.pathname))); return;
  }
  const active = url.searchParams.get('tab') || 'summary';
  res.setHeader('Content-Type', 'text/html; charset=utf-8');
  res.end(`<!doctype html><html dir="rtl" lang="fa"><head><meta charset="utf-8">${css}<style>body{margin:0;padding:16px;font-family:Arial,sans-serif;}main{max-width:1145px;margin:auto;min-width:0;}h1{font-size:28px}h2{font-size:18px}</style></head><body class="boltz-rtl action-details">${fragment(active)}<script src="/js/contract-journey-tabs.js"></script></body></html>`);
});
(async () => {
  await new Promise(resolve => server.listen(5057, '127.0.0.1', resolve));
  const browser = await chromium.launch({ executablePath: process.env.PTG_CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
  const errors = [];
  const page = await browser.newPage();
  page.on('pageerror', error => errors.push(error.message));
  const checks = [];
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('http://127.0.0.1:5057/ContractJourney/Details?contractId=17&tab=summary&lockContract=true');
    const layout = await page.evaluate(() => ({ viewport: innerWidth, documentWidth: document.documentElement.scrollWidth, links: document.querySelectorAll('[data-contract-journey-tab-link]').length, groups: document.querySelectorAll('[data-navigation-group]').length }));
    if (layout.documentWidth > layout.viewport || layout.links !== 7 || layout.groups !== 3) throw new Error('Responsive navigation check failed: ' + JSON.stringify(layout));
    await page.screenshot({ path: `${output}/journey-navigation-${width}.png`, fullPage: true });
    const link = page.locator('[data-contract-journey-tab="inventory"]');
    await link.focus();
    await link.press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-contract-journey-tab-content]').getAttribute('data-active-tab') === 'inventory');
    const focused = await page.evaluate(() => document.activeElement.getAttribute('data-contract-journey-tab'));
    if (focused !== 'inventory') throw new Error('Keyboard focus lost after navigation');
    checks.push({ width, ...layout, focused });
  }
  await page.route('**/ContractJourney/Details?**tab=costs**', route => route.abort('failed'));
  await page.locator('[data-contract-journey-tab="costs"]').click();
  const retry = page.locator('[data-contract-journey-tab-retry]');
  await retry.waitFor();
  const retryUrl = new URL(await retry.getAttribute('href'));
  if (retryUrl.searchParams.get('contractId') !== '17' || retryUrl.searchParams.get('tab') !== 'costs' || retryUrl.searchParams.get('lockContract') !== 'true') throw new Error('Requested source context lost on retry');
  if (await retry.getAttribute('data-no-spa') !== 'true') throw new Error('Fallback must be a full GET link');
  if (await page.locator('[data-contract-journey-tab-error]').getAttribute('role') !== 'alert') throw new Error('Failure is not announced');
  await page.unroute('**/ContractJourney/Details?**tab=costs**');
  await retry.click();
  await page.waitForFunction(() => document.querySelector('[data-contract-journey-tab-content]').getAttribute('data-active-tab') === 'costs');
  // A pending tab must never overwrite a different contract after SPA exit.
  let pendingRoute;
  const receivedRequest = new Promise(resolve => {
    page.route('**/ContractJourney/Details?**tab=sales**', route => {
      pendingRoute = route;
      resolve();
    });
  });
  await page.locator('[data-contract-journey-tab="sales"]').click();
  await receivedRequest;
  await page.evaluate(html => {
    document.querySelector('[data-contract-journey-page]').outerHTML = html;
  }, fragment('summary').replaceAll('contractId=17', 'contractId=18'));
  await pendingRoute.fulfill({ status: 200, contentType: 'text/html', body: fragment('sales') });
  await page.waitForTimeout(200);
  const activeAfterExit = await page.locator('[data-contract-journey-tab-content]').getAttribute('data-active-tab');
  if (activeAfterExit !== 'summary') throw new Error('Old tab response overwrote a different page');
  if (errors.length) throw new Error('Browser script errors: ' + errors.join('; '));
  const result = { checks, retryRetainedContext: true, fallbackGetSucceeded: true, oldResponseDiscardedAfterExit: true, browserErrors: errors, fixture: 'Rendered navigation markup with repository CSS/JS; no database or financial write' };
  fs.writeFileSync(`${output}/browser-checks.json`, JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
  await browser.close(); server.close();
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
