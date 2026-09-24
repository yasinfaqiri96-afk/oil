// معاش: ورودیِ «ماه» (yyyy-MM) را به دو پارامترِ year و month تبدیل می‌کند؛ و در صفحهٔ پرداخت
// دکمهٔ «انتخاب همه» فقط ردیف‌هایی را علامت می‌زند که ماندهٔ پرداخت دارند.
(function () {
    var monthInput = document.querySelector('[data-payroll-month]');
    if (monthInput) {
        monthInput.addEventListener('change', function () {
            var parts = (monthInput.value || '').split('-');
            if (parts.length !== 2) return;
            var year = document.querySelector('[data-payroll-year]');
            var mon = document.querySelector('[data-payroll-mon]');
            if (year) year.value = parts[0];
            if (mon) mon.value = String(parseInt(parts[1], 10));
        });
    }

    var selectAll = document.querySelector('[data-payroll-select-all]');
    if (selectAll) {
        selectAll.addEventListener('change', function () {
            document.querySelectorAll('[data-payroll-select]').forEach(function (box) {
                if (!box.disabled) box.checked = selectAll.checked;
            });
        });
    }
})();
