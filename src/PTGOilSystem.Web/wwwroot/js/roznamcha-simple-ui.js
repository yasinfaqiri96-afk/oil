// ساده‌سازی رابط فرم «ثبت دریافت / پرداخت» (Payments/Create و Edit).
//
// این فایل فقط لایهٔ نمایش است:
//   • برچسب‌ها با جهت سند (دریافت/پرداخت) عوض می‌شوند.
//   • «جزئیات بیشتر» فیلدهای تخصصی را جمع می‌کند (بدون disable کردن؛ همه مثل قبل post می‌شوند).
//   • خلاصهٔ زندهٔ پیش از ثبت از همان مقادیر فرم ساخته می‌شود.
//
// هیچ منطق مالی، binding یا اعتبارسنجی سمت سرور تغییر نمی‌کند.
(function () {
    'use strict';

    function init() {
        var form = document.getElementById('roznamchaForm');
        if (!form) {
            return;
        }

        var directionSelect = document.getElementById('Direction');
        var typeSelect = document.getElementById('CounterpartyType');
        var amountInput = document.getElementById('Amount');
        var currencySelect = document.getElementById('paymentCurrency');
        var cashAccountSelect = document.getElementById('cashAccountSelect');
        var partnerRadio = document.getElementById('fundingSourcePartner');
        var partnerSelect = document.getElementById('paidByPartnerSelect');
        var paymentMethodInput = document.getElementById('paymentMethodInput');
        var summary = document.getElementById('journalSummary');
        var moreToggle = document.getElementById('journalMoreToggle');
        var morePanel = document.getElementById('journalMorePanel');
        var moreIcon = moreToggle ? moreToggle.querySelector('[data-more-icon]') : null;
        var directionLabels = Array.prototype.slice.call(form.querySelectorAll('[data-direction-label]'));

        // نگاشت نوع طرف حساب به فیلدی که نام طرف حساب را نگه می‌دارد — فقط برای خلاصه.
        var partySelectIds = {
            '1': 'supplierSelect',
            '2': 'CustomerId',
            '3': 'EmployeeId',
            '4': 'DriverId',
            '9': 'ServiceProviderId',
            '10': 'cashSarrafSelect'
        };

        function partySelectFor(type) {
            var id = partySelectIds[String(type)];
            return id ? document.getElementById(id) : null;
        }

        function isReceipt() {
            return !directionSelect || directionSelect.value === '1';
        }

        function isSarrafMethod() {
            return paymentMethodInput && String(paymentMethodInput.value) === '1';
        }

        function optionText(select) {
            if (!select || select.selectedIndex < 0) {
                return '';
            }
            return (select.options[select.selectedIndex].textContent || '').trim();
        }

        // ----- جزئیات بیشتر -----
        function setMore(open) {
            if (!morePanel || !moreToggle) {
                return;
            }
            if (open) {
                morePanel.removeAttribute('hidden');
            } else {
                morePanel.setAttribute('hidden', 'hidden');
            }
            moreToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (moreIcon) {
                moreIcon.className = 'bi ' + (open ? 'bi-dash-lg' : 'bi-plus-lg');
            }
        }

        if (moreToggle && morePanel) {
            moreToggle.addEventListener('click', function () {
                setMore(morePanel.hasAttribute('hidden'));
            });

            // اگر سرور خطای اعتبارسنجی روی فیلدی از همین بخش برگردانده، بخش باز بماند.
            var invalid = morePanel.querySelector('.input-validation-error, .field-validation-error:not(:empty)');
            if (invalid) {
                setMore(true);
            }
        }

        // ----- برچسب‌های وابسته به جهت -----
        function syncDirectionLabels() {
            var receipt = isReceipt();
            directionLabels.forEach(function (label) {
                var text = receipt ? label.dataset.labelIn : label.dataset.labelOut;
                if (text) {
                    label.textContent = text;
                }
            });
        }

        // ----- خلاصهٔ زنده -----
        function partyLabel() {
            if (!typeSelect) {
                return '';
            }

            var target = partySelectFor(typeSelect.value);
            if (target) {
                return target.value ? optionText(target) : '';
            }

            // انواع بدون فهرست طرف حساب (مصرف دفتری، قرارداد، فروش، ...) با نام نوع نمایش داده می‌شوند.
            return optionText(typeSelect);
        }

        function accountLabel() {
            var text = optionText(cashAccountSelect);
            if (!text) {
                return '';
            }
            var paren = text.lastIndexOf(' (');
            return paren > -1 ? text.slice(0, paren).trim() : text;
        }

        function amountLabel() {
            var raw = amountInput ? Number(amountInput.value) : 0;
            if (!Number.isFinite(raw) || raw <= 0) {
                return '';
            }
            return raw.toLocaleString(undefined, {
                minimumFractionDigits: 2,
                maximumFractionDigits: 2
            });
        }

        function syncSummary() {
            if (!summary) {
                return;
            }

            // مسیر صراف خلاصهٔ اختصاصی خودش را دارد.
            if (isSarrafMethod()) {
                summary.hidden = true;
                return;
            }
            summary.hidden = false;

            var blank = '…';
            var amount = amountLabel() || blank;
            var currency = (currencySelect && currencySelect.value ? currencySelect.value : '').toUpperCase() || blank;
            var party = partyLabel() || blank;
            var byPartner = partnerRadio && partnerRadio.checked;
            var source = byPartner
                ? (optionText(partnerSelect) || blank)
                : (accountLabel() || blank);

            var money = amount + ' ' + currency;

            if (isReceipt()) {
                summary.textContent = money + ' از ' + party + ' به حساب ' + source + ' دریافت می‌شود.';
            } else if (byPartner) {
                summary.textContent = money + ' توسط شریک ' + source + ' به ' + party + ' پرداخت می‌شود.';
            } else {
                summary.textContent = money + ' از حساب ' + source + ' به ' + party + ' پرداخت می‌شود.';
            }

            summary.classList.toggle('is-incomplete', summary.textContent.indexOf(blank) > -1);
        }

        function refresh() {
            syncDirectionLabels();
            syncSummary();
        }

        // رویدادهای فرم: تغییر هر ورودی خلاصه و برچسب‌ها را تازه می‌کند.
        form.addEventListener('input', refresh);
        form.addEventListener('change', refresh);

        // دکمه‌های جهت و روش پرداخت رویداد change روی select ایجاد نمی‌کنند.
        form.addEventListener('click', function (event) {
            var trigger = event.target.closest('[data-direction-choice], [data-payment-method-choice]');
            if (!trigger) {
                return;
            }
            if (trigger.hasAttribute('data-payment-method-choice') && isSarrafMethod()) {
                setMore(true);
            }
            refresh();
        });

        refresh();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
