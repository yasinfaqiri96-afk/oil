/*
 * PTG UI Pick — development-only element picker.
 *
 * Loaded from _Layout only when ASPNETCORE_ENVIRONMENT=Development AND
 * PTG_UI_PICK=1. It never ships to production and touches no application state.
 *
 * Alt+Shift+P  toggle pick mode
 * click        capture the highlighted element and send it to the pick server
 * Alt+Shift+L  copy the last captured pick to the clipboard again
 * Esc          leave pick mode
 *
 * The capture is POSTed to the local pick server (tools/ui-pick/server.mjs),
 * which writes .ptg-ui-pick/last-pick.json + last-pick.md into the repository
 * for Claude Code to read. The clipboard copy is the fallback when the server
 * is not running.
 */
(function () {
  'use strict';

  if (window.__ptgUiPickLoaded) return;
  window.__ptgUiPickLoaded = true;

  var CONFIG = window.PTG_UI_PICK_CONFIG || {};
  var SERVER = CONFIG.server || 'http://127.0.0.1:5199';
  var MAX_HTML = 4000;
  var MAX_TEXT = 300;

  // Bootstrap/utility classes carry no file-location signal; the distinctive
  // ones are what Claude Code should grep for.
  var GENERIC_CLASS = /^(col|row|d|m[trblxy]?|p[trblxy]?|g|gap|text|bg|border|fw|fs|w|h|mw|mh|align|justify|flex|order|position|top|bottom|start|end|float|overflow|rounded|shadow|opacity|z|user|pe|vh|vw|min|max)-|^(row|col|container|container-fluid|btn|card|table|form-control|form-select|form-label|form-check|input-group|nav|navbar|modal|dropdown|badge|alert|list-group|collapse|show|active|disabled|fade|small|lead|visually-hidden|clearfix|sr-only)$/;

  var STYLE_KEYS = [
    'display', 'position', 'direction', 'z-index', 'overflow',
    'width', 'height', 'min-width', 'max-width',
    'margin', 'padding', 'gap',
    'flex-direction', 'align-items', 'justify-content', 'flex-wrap',
    'grid-template-columns',
    'font-family', 'font-size', 'font-weight', 'line-height', 'letter-spacing',
    'color', 'background-color', 'background-image',
    'border', 'border-radius', 'box-shadow',
    'text-align', 'white-space', 'opacity', 'transform'
  ];

  var active = false;
  var current = null;
  var lastPrompt = '';
  var box, label, badge, toast;

  /* ---------------------------------------------------------------- UI ---- */

  function injectStyles() {
    var css = [
      '#ptg-pick-box{position:fixed;pointer-events:none;z-index:2147483000;',
      'border:2px solid #2f81f7;background:rgba(47,129,247,.12);border-radius:3px;display:none}',
      '#ptg-pick-label{position:fixed;pointer-events:none;z-index:2147483001;',
      'background:#0d1117;color:#e6edf3;font:12px/1.5 Consolas,monospace;direction:ltr;',
      'padding:3px 7px;border-radius:4px;white-space:nowrap;display:none;max-width:60vw;overflow:hidden;text-overflow:ellipsis}',
      '#ptg-pick-badge{position:fixed;bottom:14px;left:14px;z-index:2147483002;',
      'background:#0d1117;color:#e6edf3;font:12px/1.6 Tahoma,sans-serif;padding:6px 12px;',
      'border-radius:999px;box-shadow:0 4px 14px rgba(0,0,0,.35);cursor:pointer;user-select:none;opacity:.75}',
      '#ptg-pick-badge[data-on="1"]{background:#2f81f7;opacity:1}',
      '#ptg-pick-toast{position:fixed;bottom:56px;left:14px;z-index:2147483003;',
      'background:#0d1117;color:#e6edf3;font:12px/1.7 Tahoma,sans-serif;padding:8px 14px;',
      'border-radius:8px;box-shadow:0 4px 14px rgba(0,0,0,.35);display:none;max-width:70vw}',
      'body[data-ptg-picking="1"]{cursor:crosshair!important}'
    ].join('');
    var style = document.createElement('style');
    style.id = 'ptg-pick-style';
    style.textContent = css;
    document.head.appendChild(style);
  }

  function buildUi() {
    injectStyles();
    box = document.createElement('div');
    box.id = 'ptg-pick-box';
    label = document.createElement('div');
    label.id = 'ptg-pick-label';
    badge = document.createElement('div');
    badge.id = 'ptg-pick-badge';
    badge.textContent = 'UI Pick — Alt+Shift+P';
    badge.addEventListener('click', function () { setActive(!active); });
    toast = document.createElement('div');
    toast.id = 'ptg-pick-toast';
    document.body.appendChild(box);
    document.body.appendChild(label);
    document.body.appendChild(badge);
    document.body.appendChild(toast);
  }

  var toastTimer = null;
  function say(msg, ms) {
    toast.textContent = msg;
    toast.style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toast.style.display = 'none'; }, ms || 3500);
  }

  function setActive(on) {
    active = on;
    badge.dataset.on = on ? '1' : '0';
    badge.textContent = on ? 'UI Pick ON — روی عنصر کلیک کنید' : 'UI Pick — Alt+Shift+P';
    document.body.dataset.ptgPicking = on ? '1' : '0';
    if (!on) {
      box.style.display = 'none';
      label.style.display = 'none';
      current = null;
    }
  }

  function highlight(el) {
    var r = el.getBoundingClientRect();
    box.style.display = 'block';
    box.style.left = r.left + 'px';
    box.style.top = r.top + 'px';
    box.style.width = r.width + 'px';
    box.style.height = r.height + 'px';

    label.textContent = describe(el) + '  ' + Math.round(r.width) + '×' + Math.round(r.height);
    label.style.display = 'block';
    var top = r.top > 26 ? r.top - 24 : r.bottom + 4;
    label.style.top = top + 'px';
    label.style.left = Math.max(4, r.left) + 'px';
  }

  /* ----------------------------------------------------------- capture ---- */

  function describe(el) {
    var s = el.tagName.toLowerCase();
    if (el.id) s += '#' + el.id;
    var cls = classList(el);
    if (cls.length) s += '.' + cls.slice(0, 4).join('.');
    return s;
  }

  function classList(el) {
    if (!el.classList) return [];
    return Array.prototype.slice.call(el.classList).filter(function (c) {
      return c.indexOf('ptg-pick') !== 0;
    });
  }

  function distinctClasses(el) {
    return classList(el).filter(function (c) { return !GENERIC_CLASS.test(c); });
  }

  function textOf(el) {
    var t = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
    return t.length > MAX_TEXT ? t.slice(0, MAX_TEXT) + '…' : t;
  }

  function datasetOf(el) {
    var out = {};
    for (var k in el.dataset) {
      if (k === 'ptgView' || k === 'ptgController' || k === 'ptgAction') continue;
      out[k] = String(el.dataset[k]).slice(0, 200);
    }
    return out;
  }

  function attributesOf(el) {
    var out = {};
    for (var i = 0; i < el.attributes.length; i++) {
      var a = el.attributes[i];
      if (a.name === 'class' || a.name === 'style' || a.name.indexOf('data-') === 0) continue;
      out[a.name] = String(a.value).slice(0, 200);
    }
    return out;
  }

  function cssPath(el) {
    var parts = [];
    var node = el;
    while (node && node.nodeType === 1 && node !== document.body && parts.length < 8) {
      if (node.id) {
        parts.unshift('#' + CSS.escape(node.id));
        break;
      }
      var seg = node.tagName.toLowerCase();
      var cls = distinctClasses(node).slice(0, 2);
      if (cls.length) seg += '.' + cls.map(function (c) { return CSS.escape(c); }).join('.');
      var parent = node.parentElement;
      if (parent) {
        var same = Array.prototype.filter.call(parent.children, function (c) {
          return c.tagName === node.tagName;
        });
        if (same.length > 1) seg += ':nth-of-type(' + (same.indexOf(node) + 1) + ')';
      }
      parts.unshift(seg);
      node = node.parentElement;
    }
    return parts.join(' > ');
  }

  /*
   * Partial-view boundaries are emitted as HTML comments by
   * TagHelpers/UiPickPartialMarkerTagHelper.cs:
   *   <!--ptg-partial-begin:Shared/_Kpi--> ... <!--ptg-partial-end:Shared/_Kpi-->
   * Walking those comment ranges gives the partial that actually rendered the
   * picked element, which the DOM tree alone cannot tell us.
   */
  function partialRanges() {
    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_COMMENT, null);
    var open = [];
    var ranges = [];
    var node;
    while ((node = walker.nextNode())) {
      var v = node.nodeValue || '';
      if (v.indexOf('ptg-partial-begin:') === 0) {
        open.push({ name: v.slice('ptg-partial-begin:'.length), begin: node });
      } else if (v.indexOf('ptg-partial-end:') === 0) {
        var name = v.slice('ptg-partial-end:'.length);
        for (var i = open.length - 1; i >= 0; i--) {
          if (open[i].name === name) {
            ranges.push({ name: name, begin: open[i].begin, end: node });
            open.splice(i, 1);
            break;
          }
        }
      }
    }
    return ranges;
  }

  /** Partials containing the element, innermost first. */
  function enclosingPartials(el) {
    var hits = partialRanges().filter(function (r) {
      var afterBegin = r.begin.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING;
      var beforeEnd = r.end.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_PRECEDING;
      return afterBegin && beforeEnd;
    });
    // Document order of the begin marker: the latest begin is the innermost.
    hits.sort(function (a, b) {
      return a.begin.compareDocumentPosition(b.begin) & Node.DOCUMENT_POSITION_FOLLOWING ? 1 : -1;
    });
    return hits.map(function (r) { return r.name; }).reverse();
  }

  function pageView() {
    var b = document.body.dataset || {};
    return b.ptgView || null;
  }

  function nearestView(el) {
    var partials = enclosingPartials(el);
    return partials.length ? partials[0] : pageView();
  }

  function viewChain(el) {
    var chain = enclosingPartials(el);
    var page = pageView();
    if (page && chain.indexOf(page) === -1) chain.push(page);
    return chain;
  }

  function ancestorsOf(el) {
    var out = [];
    var node = el.parentElement;
    while (node && node !== document.documentElement && out.length < 8) {
      out.push({
        tag: node.tagName.toLowerCase(),
        id: node.id || null,
        classes: classList(node).slice(0, 8),
        view: (node.dataset && node.dataset.ptgView) || null
      });
      node = node.parentElement;
    }
    return out;
  }

  function childrenOf(el) {
    return Array.prototype.slice.call(el.children, 0, 12).map(function (c) {
      return {
        tag: c.tagName.toLowerCase(),
        id: c.id || null,
        classes: classList(c).slice(0, 6),
        text: textOf(c).slice(0, 80)
      };
    });
  }

  function stylesOf(el) {
    var cs = getComputedStyle(el);
    var out = {};
    STYLE_KEYS.forEach(function (k) {
      var v = cs.getPropertyValue(k);
      if (v && v !== 'none' && v !== 'normal' && v !== 'auto') out[k] = v.trim();
    });
    return out;
  }

  function pageAssets() {
    return Array.prototype.map.call(
      document.querySelectorAll('[data-ptg-page-asset]'),
      function (n) { return n.getAttribute('data-ptg-page-asset'); }
    );
  }

  function stylesheets() {
    return Array.prototype.map.call(
      document.querySelectorAll('link[rel="stylesheet"]'),
      function (n) { return n.getAttribute('href').split('?')[0]; }
    ).filter(function (h) { return h.indexOf('/vendor/') === -1; });
  }

  function capture(el) {
    var r = el.getBoundingClientRect();
    var html = el.outerHTML || '';
    return {
      source_tool: 'ptg-ui-pick',
      page: {
        url: location.href,
        path: location.pathname + location.search,
        title: document.title,
        controller: (document.body.dataset || {}).ptgController || null,
        action: (document.body.dataset || {}).ptgAction || null,
        viewport: { width: window.innerWidth, height: window.innerHeight },
        scroll: { x: Math.round(window.scrollX), y: Math.round(window.scrollY) },
        dir: document.documentElement.getAttribute('dir') || null,
        lang: document.documentElement.getAttribute('lang') || null
      },
      element: {
        tag: el.tagName.toLowerCase(),
        id: el.id || null,
        classes: classList(el),
        dataset: datasetOf(el),
        attributes: attributesOf(el),
        text: textOf(el),
        cssPath: cssPath(el),
        rect: {
          x: Math.round(r.left), y: Math.round(r.top),
          width: Math.round(r.width), height: Math.round(r.height)
        },
        outerHTML: html.length > MAX_HTML ? html.slice(0, MAX_HTML) + '\n<!-- truncated -->' : html
      },
      source: {
        view: nearestView(el),
        viewChain: viewChain(el),
        classHints: distinctClasses(el),
        pageAssets: pageAssets(),
        stylesheets: stylesheets()
      },
      ancestors: ancestorsOf(el),
      children: childrenOf(el),
      computedStyles: stylesOf(el)
    };
  }

  function promptText(p) {
    var el = p.element;
    return [
      'UI PICK — این عنصر را در پروژه پیدا کن و فقط همین بخش را اصلاح کن.',
      'URL: ' + p.page.url,
      'View: ' + (p.source.view || '(unknown)'),
      'View chain: ' + (p.source.viewChain.join(' -> ') || '(none)'),
      'Selector: ' + el.cssPath,
      'Tag: ' + el.tag + (el.id ? ' #' + el.id : ''),
      'Classes: ' + (el.classes.join(' ') || '(none)'),
      'Text: ' + (el.text || '(empty)'),
      'outerHTML:',
      el.outerHTML
    ].join('\n');
  }

  function copy(text) {
    lastPrompt = text;
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).catch(function () {});
    }
    return Promise.resolve();
  }

  function send(el) {
    var payload = capture(el);
    var prompt = promptText(payload);
    lastPrompt = prompt;

    fetch(SERVER + '/pick', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })
      .then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        // The VS Code bridge extension owns the clipboard from here on, so the
        // page must not overwrite it with its own copy of the same prompt.
        say('✅ به Claude Code تحویل شد — در پنل Claude: Ctrl+V سپس Enter');
      })
      .catch(function () {
        copy(prompt);
        say('⚠️ pick server در دسترس نیست. اطلاعات در Clipboard کپی شد — در Claude Code Paste کنید.', 6000);
      });
  }

  /* ------------------------------------------------------------ events ---- */

  function onMove(e) {
    if (!active) return;
    var el = e.target;
    if (!el || el.id === 'ptg-pick-badge' || el.id === 'ptg-pick-toast') return;
    current = el;
    highlight(el);
  }

  function onClick(e) {
    if (!active) return;
    if (e.target && (e.target.id === 'ptg-pick-badge' || e.target.id === 'ptg-pick-toast')) return;
    e.preventDefault();
    e.stopPropagation();
    var el = current || e.target;
    send(el);
    setActive(false);
  }

  function onKey(e) {
    if (e.altKey && e.shiftKey && (e.key === 'P' || e.key === 'p')) {
      e.preventDefault();
      setActive(!active);
      return;
    }
    if (e.altKey && e.shiftKey && (e.key === 'L' || e.key === 'l')) {
      e.preventDefault();
      if (lastPrompt) {
        copy(lastPrompt);
        say('📋 آخرین انتخاب دوباره در Clipboard کپی شد.');
      } else {
        say('هنوز عنصری انتخاب نشده است.');
      }
      return;
    }
    if (e.key === 'Escape' && active) setActive(false);
  }

  function start() {
    buildUi();
    document.addEventListener('mousemove', onMove, true);
    document.addEventListener('click', onClick, true);
    document.addEventListener('keydown', onKey, true);
    // SPA navigation replaces <main>; the overlay lives on <body> so it survives,
    // but the highlight box must be dropped when the page content changes.
    window.addEventListener('popstate', function () { setActive(false); });
    document.addEventListener('ptg:spa-navigated', function () { setActive(false); });
    console.info('[ptg-ui-pick] ready — Alt+Shift+P to pick an element.');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
