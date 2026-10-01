// «سهم شرکت» و «شرکای دیگر» در فرم قرارداد شراکتی (ایجاد و ویرایش).
//
// مالک دفتر = شریکی که در اطلاعات شرکت به‌عنوان مالک دفترِ شرکتِ انتخاب‌شده ثبت شده است.
// «سهم شرکت» همان سهمِ مالک دفتر است و سرور آن را به سطرِ همان شریک تبدیل می‌کند؛ پس اینجا
// مالک دفتر از فهرست «شرکای دیگر» برداشته می‌شود تا دوباره انتخاب نشود. با عوض‌شدنِ شرکت،
// مالک دفترِ شرکتِ تازه برداشته و مالک قبلی دوباره قابل انتخاب می‌شود. جمع فقط نمایشی است؛
// اعتبارسنجیِ ۱۰۰٪ همان قاعدهٔ سرور است.
(function () {
    'use strict';

    var MissingOwnerMessage = 'مالک دفتر این قرارداد مشخص نیست؛ تا آن را در اطلاعات شرکت تعیین نکنید، پرداخت از صندوق شرکت برای این قرارداد ثبت نمی‌شود.';
    var ZeroCompanyShareMessage = 'سهم شرکت صفر است؛ پرداخت از صندوق شرکت برای این قرارداد ثبت نمی‌شود.';

    function parseShare(value) {
        var text = String(value || '')
            .replace(/[۰-۹٠-٩]/g, function (character) {
                var code = character.charCodeAt(0);
                return String.fromCharCode((code >= 0x06F0 ? code - 0x06F0 : code - 0x0660) + 48);
            })
            .replace(/[٫,\/]/g, '.')
            .trim();
        var number = Number.parseFloat(text);
        return Number.isFinite(number) ? number : 0;
    }

    function formatShare(value) {
        return (Math.round(value * 10000) / 10000).toString();
    }

    function init() {
        var section = document.getElementById('partnerSection');
        var rowsBody = document.getElementById('partnerRowsBody');
        var template = document.getElementById('partnerRowTemplate');
        var companySelect = document.getElementById('CompanyId');
        var ownershipType = document.getElementById('ownershipType');
        if (!section || !rowsBody || !companySelect || !ownershipType) {
            return;
        }

        var companyShare = section.querySelector('[data-company-share]');
        var ownerLabel = section.querySelector('[data-company-share-owner]');
        var totalLabel = section.querySelector('[data-share-total]');
        var note = section.querySelector('[data-book-owner-note]');

        var owners = {};
        try {
            owners = JSON.parse(section.getAttribute('data-book-owners') || '{}');
        } catch (e) {
            owners = {};
        }

        function partnerName(partnerKey) {
            var source = template && template.content ? template.content.querySelector('select') : rowsBody.querySelector('select');
            var option = source ? Array.from(source.options).find(function (item) { return item.value === partnerKey; }) : null;
            return option ? option.textContent.trim() : '';
        }

        function refresh() {
            var ownerId = owners[companySelect.value];
            var ownerKey = ownerId === undefined || ownerId === null ? '' : String(ownerId);
            var isPartnership = ownershipType.value === '2';
            var total = 0;

            if (companyShare) {
                // شرکتِ بدون مالک دفتر سهمی ندارد که به کسی نگاشت شود؛ فیلد ارسال نمی‌شود.
                companyShare.disabled = ownerKey === '';
                if (!companyShare.disabled) {
                    total += parseShare(companyShare.value);
                }
            }

            if (ownerLabel) {
                var ownerName = ownerKey === '' ? '' : partnerName(ownerKey);
                ownerLabel.textContent = !companySelect.value
                    ? ''
                    : ownerKey === ''
                        ? 'این شرکت مالک دفتر ندارد.'
                        : 'مالک دفتر: ' + (ownerName || '—');
            }

            rowsBody.querySelectorAll('[data-partner-row]').forEach(function (row) {
                var select = row.querySelector('select');
                if (select) {
                    Array.from(select.options).forEach(function (option) {
                        var isOwner = ownerKey !== '' && option.value === ownerKey;
                        option.hidden = isOwner;
                        option.disabled = isOwner;
                    });
                    if (ownerKey !== '' && select.value === ownerKey) {
                        select.value = '';
                    }
                }

                var shareInput = row.querySelector('input');
                if (shareInput) {
                    total += parseShare(shareInput.value);
                }
            });

            if (totalLabel) {
                totalLabel.textContent = 'مجموع: ' + formatShare(total) + '٪';
                totalLabel.classList.toggle('is-warning', Math.abs(total - 100) > 0.0001);
            }

            if (!note) {
                return;
            }

            var message = '';
            if (isPartnership && companySelect.value) {
                if (ownerKey === '') {
                    message = MissingOwnerMessage;
                } else if (companyShare && parseShare(companyShare.value) <= 0) {
                    message = ZeroCompanyShareMessage;
                }
            }

            note.textContent = message;
            note.classList.toggle('d-none', message === '');
        }

        companySelect.addEventListener('change', refresh);
        ownershipType.addEventListener('change', refresh);
        section.addEventListener('change', refresh);
        section.addEventListener('input', refresh);
        // افزودن/حذفِ ردیف شریک از اسکریپتِ خودِ صفحه انجام می‌شود؛ همین‌جا فقط تغییر ردیف‌ها شنیده می‌شود.
        new MutationObserver(refresh).observe(rowsBody, { childList: true });
        refresh();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
