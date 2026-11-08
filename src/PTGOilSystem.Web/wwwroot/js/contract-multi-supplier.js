// فرم قرارداد خرید چندتأمین‌کننده و چاپ صفحهٔ جزئیات آن.
// محاسبه‌های این فایل فقط نمایشی است؛ مقدار، قیمت و اعتبارسنجی نهایی همیشه در سرور انجام می‌شود.
(function () {
    'use strict';

    var printButton = document.querySelector('[data-print-page]');
    if (printButton) {
        printButton.addEventListener('click', function () { window.print(); });
    }

    var section = document.querySelector('[data-multi-supplier-section]');
    if (!section) return;

    var flag = document.querySelector('[data-multi-supplier-flag]');
    var form = section.closest('form');
    var body = section.querySelector('[data-purchase-lines-body]');
    var template = section.querySelector('[data-purchase-line-template]');
    var pricingMethod = document.getElementById('pricingMethod');
    var currency = document.getElementById('contractCurrency');
    var numberInput = document.querySelector('[data-contract-number-auto]');
    var linesError = section.querySelector('[data-purchase-lines-error]');
    var modeButtons = Array.prototype.slice.call(document.querySelectorAll('[data-contract-mode]'));
    var singleOnly = Array.prototype.slice.call(document.querySelectorAll('[data-single-contract-only]'));
    var formatter = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
    var quantityFormatter = new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 });
    var submitFailed = false;

    function isMulti() {
        return flag.value === 'True' || flag.value === 'true';
    }

    function number(input) {
        var value = Number.parseFloat(input ? input.value : '');
        return Number.isFinite(value) ? value : null;
    }

    function rows() {
        return Array.prototype.slice.call(body.querySelectorAll('[data-purchase-line]'));
    }

    function setSingleOnlyDisabled(disabled) {
        singleOnly.forEach(function (element) {
            element.classList.toggle('d-none', disabled);
            element.querySelectorAll('input, select, textarea').forEach(function (field) {
                field.disabled = disabled;
            });
        });
    }

    function setSectionFieldsDisabled(disabled) {
        section.querySelectorAll('[data-purchase-lines-body] input, [data-purchase-lines-body] select').forEach(function (field) {
            field.disabled = disabled;
        });
    }

    function applyMode(multi) {
        flag.value = multi ? 'True' : 'False';
        modeButtons.forEach(function (button) {
            var selected = (button.dataset.contractMode === 'multi') === multi;
            button.classList.toggle('is-selected', selected);
            button.setAttribute('aria-pressed', selected ? 'true' : 'false');
        });
        section.classList.toggle('d-none', !multi);
        setSectionFieldsDisabled(!multi);

        if (multi && pricingMethod) {
            pricingMethod.value = '1';
        }
        setSingleOnlyDisabled(false);
        if (pricingMethod) {
            pricingMethod.dispatchEvent(new Event('change', { bubbles: true }));
        }
        setSingleOnlyDisabled(multi);

        if (numberInput) {
            numberInput.value = (multi ? numberInput.dataset.multiSupplierNumber : numberInput.dataset.purchaseNumber) || numberInput.value;
        }
        refreshTotals();
    }

    function reindex() {
        rows().forEach(function (row, index) {
            row.querySelectorAll('[data-line-field]').forEach(function (field) {
                field.name = field.dataset.lineField.replace('__index__', String(index));
            });
        });
    }

    function addRow() {
        var row = template.content.firstElementChild.cloneNode(true);
        body.appendChild(row);
        reindex();
        refreshTotals();
    }

    function refreshTotals() {
        var totalQuantity = 0;
        var totalValue = 0;
        var pricedQuantity = 0;
        var suppliers = {};
        rows().forEach(function (row) {
            var quantity = number(row.querySelector('[data-line-quantity]'));
            var price = number(row.querySelector('[data-line-price]'));
            var supplier = row.querySelector('[data-line-supplier]').value;
            var cell = row.querySelector('[data-line-value]');
            if (supplier) suppliers[supplier] = true;
            if (quantity && quantity > 0) totalQuantity += quantity;
            if (quantity && quantity > 0 && price && price > 0) {
                var value = quantity * price;
                totalValue += value;
                pricedQuantity += quantity;
                cell.textContent = formatter.format(value);
            } else {
                cell.textContent = '-';
            }
        });

        var code = currency && currency.value ? currency.value : 'USD';
        section.querySelector('[data-total-quantity]').textContent = quantityFormatter.format(totalQuantity) + ' MT';
        section.querySelector('[data-total-value]').textContent = formatter.format(totalValue) + ' ' + code;
        section.querySelector('[data-total-suppliers]').textContent = String(Object.keys(suppliers).length);
        section.querySelector('[data-total-average]').textContent = pricedQuantity > 0
            ? formatter.format(totalValue / pricedQuantity) + ' ' + code
            : '-';
    }

    // ساخت سریع تأمین‌کننده/محصول فقط به همان select گزینه می‌افزاید؛ ردیف‌های دیگر هم باید آن را داشته باشند.
    function shareNewOptions(changed) {
        var attribute = changed.hasAttribute('data-line-supplier') ? '[data-line-supplier]' : '[data-line-product]';
        var targets = Array.prototype.slice.call(body.querySelectorAll(attribute))
            .concat(Array.prototype.slice.call(template.content.querySelectorAll(attribute)));
        Array.prototype.forEach.call(changed.options, function (option) {
            if (!option.value) return;
            targets.forEach(function (target) {
                if (target === changed) return;
                var exists = Array.prototype.some.call(target.options, function (candidate) { return candidate.value === option.value; });
                if (!exists) target.add(new Option(option.text, option.value));
            });
        });
    }

    function setLineError(row, field, message) {
        var slot = row.querySelector('[data-line-error="' + field + '"]');
        if (slot) slot.textContent = message;
    }

    function validateRows() {
        var filled = 0;
        var valid = true;
        rows().forEach(function (row) {
            var supplier = row.querySelector('[data-line-supplier]').value;
            var product = row.querySelector('[data-line-product]').value;
            var quantityInput = row.querySelector('[data-line-quantity]');
            var priceInput = row.querySelector('[data-line-price]');
            var empty = !supplier && !product && !quantityInput.value && !priceInput.value;
            var errors = {
                SupplierId: !empty && !supplier ? 'تأمین‌کننده را انتخاب کنید.' : '',
                ProductId: !empty && !product ? 'محصول را انتخاب کنید.' : '',
                QuantityMt: !empty && !(number(quantityInput) > 0) ? 'مقدار باید بزرگ‌تر از صفر باشد.' : '',
                UnitPrice: !empty && !(number(priceInput) > 0) ? 'فی تن باید بزرگ‌تر از صفر باشد.' : ''
            };
            Object.keys(errors).forEach(function (field) {
                setLineError(row, field, errors[field]);
                if (errors[field]) valid = false;
            });
            if (!empty) filled += 1;
        });

        var enough = filled >= 2;
        linesError.textContent = enough ? '' : 'قرارداد چندتأمین‌کننده حداقل دو ردیف خرید لازم دارد؛ برای یک ردیف، قرارداد عادی ثبت کنید.';
        return valid && enough;
    }

    modeButtons.forEach(function (button) {
        button.addEventListener('click', function () {
            applyMode(button.dataset.contractMode === 'multi');
        });
    });

    section.querySelector('[data-add-purchase-line]').addEventListener('click', addRow);

    body.addEventListener('click', function (event) {
        var remove = event.target.closest('[data-remove-purchase-line]');
        if (!remove) return;
        remove.closest('[data-purchase-line]').remove();
        if (!rows().length) addRow();
        reindex();
        refreshTotals();
    });

    function revalidateAfterFailedSubmit() {
        if (submitFailed) validateRows();
    }

    body.addEventListener('input', function () {
        refreshTotals();
        revalidateAfterFailedSubmit();
    });
    body.addEventListener('change', function (event) {
        if (event.target.matches('[data-line-supplier], [data-line-product]')) {
            shareNewOptions(event.target);
        }
        refreshTotals();
        revalidateAfterFailedSubmit();
    });

    if (currency) {
        currency.addEventListener('change', function () {
            if (isMulti()) setSingleOnlyDisabled(true);
            refreshTotals();
        });
    }

    // form-bridge رویداد submit را در window می‌گیرد، پس بررسی باید پیش از آن و روی کلیک دکمه باشد.
    // فشردن Enter هم در مرورگر همین کلیک را روی دکمهٔ پیش‌فرض می‌سازد.
    if (form) {
        form.addEventListener('click', function (event) {
            if (!event.target.closest('button[type="submit"]')) return;
            if (isMulti() && !validateRows()) {
                submitFailed = true;
                event.preventDefault();
                event.stopImmediatePropagation();
                section.scrollIntoView({ block: 'nearest' });
            }
        }, true);
    }

    // بعد از خطای سرور، صفحه دوباره ساخته می‌شود؛ خطاهای نمایش‌داده‌شده با اصلاح ردیف‌ها پاک می‌شوند.
    submitFailed = Array.prototype.some.call(
        section.querySelectorAll('[data-line-error], [data-purchase-lines-error]'),
        function (element) { return element.textContent.trim().length > 0; });

    reindex();
    applyMode(isMulti());
})();
