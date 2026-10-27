/* Presentation only: no amount, FX, stock or posting calculations. */
(function () {
    'use strict';
    const isEn = (document.documentElement.lang || 'fa').toLowerCase().startsWith('en');
    function T(fa, en) { return isEn ? en : fa; }
    // The picker is the catalog: the server lists only widgets this user may see.
    let widgetIds = [];
    let defaultIds = [];
    let selected = [];
    let pending = [];
    function readCatalog() {
        const inputs = Array.from(document.querySelectorAll('[data-mashal-widget-choice]'));
        widgetIds = inputs.map(input => input.value);
        defaultIds = inputs.filter(input => input.hasAttribute('data-mashal-widget-default')).map(input => input.value);
    }
    function loadWidget(el) {
        const body = el.querySelector('[data-mashal-widget-src]');
        if (!body || body.dataset.mashalWidgetState) return;
        body.dataset.mashalWidgetState = 'loading';
        fetch(body.dataset.mashalWidgetSrc, { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(function (response) { if (!response.ok) throw new Error(String(response.status)); return response.text(); })
            .then(function (html) { body.innerHTML = html; body.dataset.mashalWidgetState = 'ready'; })
            .catch(function () {
                const message = document.createElement('p');
                message.className = 'mashal-widget-empty';
                message.textContent = T('بارگذاری این ابزارک ممکن نشد.', 'This widget could not be loaded.');
                body.replaceChildren(message);
                delete body.dataset.mashalWidgetState;
            });
    }
    function key() {
        const user = document.querySelector('meta[name="ptg-offline"]');
        try { return 'mashal-dashboard-widgets:' + JSON.parse(user?.content || '{}').u; }
        catch (_) { return 'mashal-dashboard-widgets'; }
    }
    function applyWidgets() {
        document.querySelectorAll('[data-mashal-widget]').forEach(function (el) {
            el.hidden = !selected.includes(el.dataset.mashalWidget);
            if (!el.hidden) loadWidget(el);
        });
    }
    function syncChoices() {
        document.querySelectorAll('[data-mashal-widget-choice]').forEach(function (input) {
            input.checked = pending.includes(input.value);
        });
        const all = document.querySelector('[data-mashal-widgets-all]');
        if (all) { all.checked = pending.length === widgetIds.length; all.indeterminate = pending.length > 0 && pending.length < widgetIds.length; }
        const apply = document.querySelector('[data-mashal-widgets-apply]');
        if (apply) apply.disabled = widgetIds.every(id => pending.includes(id) === selected.includes(id));
    }
    function initReceipt() {
        const page = document.querySelector('.mashal-invoice-receipt');
        if (!page || page.dataset.mashalReceiptReady) return;
        const form = page.querySelector('#roznamchaForm');
        const party = form?.querySelector('.mashal-payment-party');
        const grid = party?.querySelector('.journal-primary-grid');
        if (!grid) return;
        page.dataset.mashalReceiptReady = 'true';
        // Move existing controls intact: names, values, validation and owning
        // cash/bank fieldset are preserved. No payment is created or recalculated.
        for (const name of ['Amount', 'Description']) {
            const field = form.querySelector('[name="' + name + '"]')?.closest('.ak-field');
            if (field) { field.classList.add('mashal-receipt-' + name.toLowerCase()); grid.append(field); }
        }
        const avatar = document.createElement('img');
        avatar.className = 'mashal-receipt-person';
        avatar.src = '/images/mashal-receipt-avatar.png';
        avatar.alt = '';
        avatar.width = 78;
        avatar.height = 113;
        const card = document.createElement('div');
        card.className = 'mashal-receipt-card';
        party.insertBefore(card, grid);
        card.append(avatar, grid);
        const options = document.createElement('details');
        options.className = 'mashal-receipt-options';
        const summary = document.createElement('summary');
        summary.textContent = T('نوع عملیات و روش دریافت / پرداخت', 'Operation and payment method');
        options.append(summary);
        for (const selector of ['.journal-direction-segmented', '.journal-mode-bar']) {
            const control = form.querySelector(selector);
            if (control) options.append(control);
        }
        const partyType = grid.querySelector('[name="CounterpartyType"]')?.closest('.ak-field');
        if (partyType) {
            const partyOptions = document.createElement('details');
            partyOptions.className = 'mashal-receipt-party-options';
            const label = document.createElement('summary');
            label.textContent = T('نوع طرف حساب', 'Counterparty type');
            partyOptions.append(label, partyType);
            grid.append(partyOptions);
        }
        const usdNote = form.querySelector('#paymentUsdNote');
        if (usdNote) options.append(usdNote);
        form.querySelector('[data-more-toggle]').before(options);
        const total = form.querySelector('[data-journal-summary]');
        const bank = form.querySelector('.mashal-payment-bank');
        if (total && bank) {
            const totals = document.createElement('div');
            totals.className = 'mashal-receipt-totals';
            const balance = form.querySelector('#journalPartyBalance');
            bank.before(totals);
            if (balance) totals.append(balance);
            totals.append(total);
        }
    }
    function init() {
        initReceipt();
        if (!document.querySelector('.dashboard-page')) return;
        readCatalog();
        try {
            const saved = JSON.parse(localStorage.getItem(key()));
            // A saved widget the user can no longer see is dropped, not a reason to reset the layout.
            selected = Array.isArray(saved) ? saved.filter(id => widgetIds.includes(id)) : defaultIds.slice();
        } catch (_) { selected = defaultIds.slice(); }
        pending = selected.slice(); applyWidgets(); syncChoices();
    }
    document.addEventListener('show.bs.modal', function (event) {
        if (event.target.id !== 'mashalWidgetPicker') return;
        pending = selected.slice(); syncChoices();
    });
    document.addEventListener('change', function (event) {
        if (event.target.matches('[data-mashal-widget-choice]')) {
            pending = Array.from(document.querySelectorAll('[data-mashal-widget-choice]:checked'), input => input.value); syncChoices();
        }
        if (event.target.matches('[data-mashal-widgets-all]')) {
            pending = event.target.checked ? widgetIds.slice() : []; syncChoices();
        }
    });
    function save() {
        try { localStorage.setItem(key(), JSON.stringify(selected)); return true; }
        catch (_) { return false; }
    }
    document.addEventListener('click', function (event) {
        if (event.target.closest('[data-mashal-widgets-apply]')) {
            selected = pending.slice(); applyWidgets(); save();
            bootstrap.Modal.getInstance(document.getElementById('mashalWidgetPicker'))?.hide();
        }
        if (event.target.closest('[data-mashal-save]')) {
            const status = document.querySelector('[data-mashal-save-status]');
            if (status) status.textContent = save() ? T('چیدمان داشبورد ذخیره شد.', 'Dashboard layout saved.') : T('ذخیره چیدمان در مرورگر ممکن نیست.', 'The layout could not be saved in this browser.');
        }
    });
    document.addEventListener('input', function (event) {
        if (event.target.matches('[data-mashal-nav-search]')) {
            const query = event.target.value.trim();
            document.querySelectorAll('.boltz-sidebar-nav > ul > li').forEach(el => { el.hidden = !el.textContent.includes(query); });
        }
        if (event.target.matches('[data-mashal-settings-search]')) {
            const query = event.target.value.trim();
            document.querySelectorAll('.mashal-settings-group a').forEach(el => { el.hidden = !el.textContent.includes(query); });
        }
    });
    document.addEventListener('DOMContentLoaded', init);
    window.addEventListener('ptg:page-ready', init);
})();
