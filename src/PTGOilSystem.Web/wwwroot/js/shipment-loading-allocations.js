// سهم دقیق بارگیری‌ها در فرم ثبت/ویرایش محموله.
//
// کاربر فقط «مقدار» را وارد می‌کند؛ نرخ خرید هرگز دستی گرفته نمی‌شود و همیشه از نرخ قطعی
// همان بارگیری (LoadingRegister.LoadingPriceUsd) خوانده و فقط نمایش داده می‌شود.
//
// این ماژول عمداً مستقل از اسکریپت درون‌خطی فرم است: با MutationObserver ردیف‌های سهم را
// جفت/پاک می‌کند و ایندکس‌ها را هم‌زمان نگه می‌دارد، پس منطق افزودن/حذف ردیف قرارداد
// دست‌نخورده می‌ماند. اعتبارسنجی نهایی همیشه سمت سرور است؛ اینجا فقط راهنمای کاربر است.
(function () {
    'use strict';

    const root = document.querySelector('[data-loading-allocations]');
    if (!root) { return; }

    const body = document.querySelector('[data-allocation-body]');
    const template = document.getElementById('loading-allocation-row-template');
    if (!body || !template) { return; }

    const endpoint = root.dataset.loadingEndpoint;
    const shipmentId = root.dataset.shipmentId || '';
    const labels = JSON.parse(root.dataset.labels || '{}');

    const number = (value) => Number.parseFloat(value) || 0;
    const format = (value) => number(value).toFixed(4);
    const formatMoney = (value) => number(value).toLocaleString('en-US', { maximumFractionDigits: 2 });

    function allocationRows() {
        return Array.from(body.querySelectorAll('[data-allocation-row]'));
    }

    function pairedRow(row) {
        const next = row.nextElementSibling;
        return next && next.matches('[data-allocation-loadings]') ? next : null;
    }

    // هر ردیف قرارداد باید دقیقاً یک ردیف سهم داشته باشد؛ ردیف‌های یتیم حذف می‌شوند.
    function ensurePairs() {
        allocationRows().forEach(function (row) {
            if (!pairedRow(row)) {
                const fragment = template.content.cloneNode(true);
                row.after(fragment);
            }
        });

        Array.from(body.querySelectorAll('[data-allocation-loadings]')).forEach(function (panel) {
            const previous = panel.previousElementSibling;
            if (!previous || !previous.matches('[data-allocation-row]')) {
                panel.remove();
            }
        });
    }

    // ایندکس فیلدهای سهم باید همیشه با ایندکس ردیف قرارداد یکی باشد (model binding).
    function syncIndexes() {
        allocationRows().forEach(function (row, index) {
            const panel = pairedRow(row);
            if (!panel) { return; }
            panel.dataset.index = index.toString();
            panel.querySelectorAll('[name]').forEach(function (field) {
                field.name = field.name.replace(
                    /ContractAllocations\[[^\]]*\]\.LoadingAllocations\[(\d+)\]/,
                    'ContractAllocations[' + index + '].LoadingAllocations[$1]');
            });
        });
    }

    function setStatus(panel, message, level) {
        const status = panel.querySelector('[data-loading-status]');
        if (!status) { return; }
        status.textContent = message || '';
        status.hidden = !message;
        status.classList.remove('alert-info', 'alert-warning', 'alert-danger');
        status.classList.add('alert-' + (level || 'info'));
    }

    function contractQuantity(row) {
        return number(row.querySelector('[data-alloc-qty]')?.value);
    }

    function refreshTotals(panel) {
        const row = panel.previousElementSibling;
        const inputs = Array.from(panel.querySelectorAll('[data-loading-qty]'));
        const total = inputs.reduce((sum, input) => sum + number(input.value), 0);
        const totalEl = panel.querySelector('[data-loading-total]');
        if (totalEl) { totalEl.textContent = format(total); }

        const badge = panel.querySelector('[data-loading-summary-badge]');
        const allocated = row ? contractQuantity(row) : 0;
        const used = inputs.some((input) => number(input.value) > 0);

        if (badge) {
            badge.hidden = !used;
            if (used) {
                const matches = Math.abs(total - allocated) <= 0.0001;
                badge.textContent = matches
                    ? labels.matched
                    : (labels.mismatch || '').replace('{0}', format(total)).replace('{1}', format(allocated));
                badge.classList.toggle('text-bg-success', matches);
                badge.classList.toggle('text-bg-warning', !matches);
                badge.classList.remove('text-bg-secondary');
            }
        }

        if (used && Math.abs(total - allocated) > 0.0001) {
            setStatus(panel, (labels.mismatch || '').replace('{0}', format(total)).replace('{1}', format(allocated)), 'warning');
        } else {
            setStatus(panel, '', 'info');
        }
    }

    function renderLoadings(panel, data) {
        const tbody = panel.querySelector('[data-loading-body]');
        const wrap = panel.querySelector('[data-loading-table-wrap]');
        const index = panel.dataset.index;
        if (!tbody || !wrap) { return; }

        const seed = JSON.parse(panel.dataset.seed || '[]');
        const seededById = {};
        seed.forEach(function (item) { seededById[item.loadingRegisterId] = item.quantityMt; });

        const rows = data.loadings || [];
        tbody.replaceChildren();

        rows.forEach(function (loading, position) {
            const tr = document.createElement('tr');
            const seeded = seededById[loading.loadingRegisterId];
            // باقی‌ماندهٔ قابل تخصیص شامل سهم فعلی همین محموله است (سرور همین را برمی‌گرداند).
            const max = number(loading.remainingQuantityMt);

            tr.innerHTML =
                '<td>' + escapeHtml(loading.label) + '</td>' +
                '<td>' + escapeHtml((loading.loadingDate || '').slice(0, 10)) + '</td>' +
                '<td class="text-end">' + format(loading.loadedQuantityMt) + '</td>' +
                '<td class="text-end">' + format(loading.allocatedQuantityMt) + '</td>' +
                '<td class="text-end">' + format(max) + '</td>' +
                '<td class="text-end">' + (loading.loadingPriceUsd
                    ? formatMoney(loading.loadingPriceUsd)
                    : '<span class="text-muted">' + escapeHtml(labels.noPrice || '') + '</span>') + '</td>' +
                '<td class="text-end"></td>';

            const hidden = document.createElement('input');
            hidden.type = 'hidden';
            hidden.name = 'ContractAllocations[' + index + '].LoadingAllocations[' + position + '].LoadingRegisterId';
            hidden.value = loading.loadingRegisterId;

            const input = document.createElement('input');
            input.type = 'number';
            input.step = '0.0001';
            input.min = '0';
            input.max = format(max);
            input.className = 'ak-input text-end';
            input.name = 'ContractAllocations[' + index + '].LoadingAllocations[' + position + '].QuantityMt';
            input.value = seeded > 0 ? format(seeded) : '';
            input.setAttribute('data-loading-qty', '');
            input.setAttribute('aria-label', (labels.quantityFor || '') + ' ' + loading.label);

            const cell = tr.lastElementChild;
            cell.appendChild(hidden);
            cell.appendChild(input);
            tbody.appendChild(tr);
        });

        wrap.hidden = rows.length === 0;
        if (rows.length === 0) {
            setStatus(panel, labels.noLoadings, 'warning');
        } else {
            refreshTotals(panel);
        }
    }

    function escapeHtml(value) {
        const div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    async function loadFor(row) {
        const panel = pairedRow(row);
        if (!panel) { return; }
        const contractId = row.querySelector('[data-contract-select]')?.value;

        if (!contractId) {
            panel.querySelector('[data-loading-body]')?.replaceChildren();
            const wrap = panel.querySelector('[data-loading-table-wrap]');
            if (wrap) { wrap.hidden = true; }
            setStatus(panel, labels.selectContract, 'info');
            return;
        }

        const requestId = (Number(panel.dataset.requestId || 0) + 1).toString();
        panel.dataset.requestId = requestId;
        setStatus(panel, labels.loading, 'info');

        try {
            const url = endpoint + '?contractId=' + encodeURIComponent(contractId)
                + (shipmentId ? '&shipmentId=' + encodeURIComponent(shipmentId) : '');
            const response = await fetch(url, { headers: { Accept: 'application/json' } });
            if (!response.ok) { throw new Error('loading request failed'); }

            const data = await response.json();
            if (panel.dataset.requestId !== requestId) { return; }
            renderLoadings(panel, data);
        } catch (error) {
            if (panel.dataset.requestId !== requestId) { return; }
            setStatus(panel, labels.loadError, 'danger');
        }
    }

    const observer = new MutationObserver(function () {
        ensurePairs();
        syncIndexes();
    });
    observer.observe(body, { childList: true });

    body.addEventListener('change', function (event) {
        const row = event.target.closest('[data-allocation-row]');
        if (row && event.target.matches('[data-contract-select]')) {
            const panel = pairedRow(row);
            // انتخاب قرارداد جدید یعنی سهم‌های قبلی دیگر معتبر نیستند.
            if (panel) { panel.dataset.seed = '[]'; }
            loadFor(row);
        }
    });

    body.addEventListener('input', function (event) {
        if (event.target.matches('[data-loading-qty]')) {
            const panel = event.target.closest('[data-allocation-loadings]');
            if (panel) { refreshTotals(panel); }
        } else if (event.target.matches('[data-alloc-qty]')) {
            const row = event.target.closest('[data-allocation-row]');
            const panel = row ? pairedRow(row) : null;
            if (panel) { refreshTotals(panel); }
        }
    });

    body.closest('form')?.addEventListener('submit', syncIndexes);

    ensurePairs();
    syncIndexes();
    allocationRows().forEach(function (row) {
        if (row.querySelector('[data-contract-select]')?.value) {
            loadFor(row);
        }
    });
})();
