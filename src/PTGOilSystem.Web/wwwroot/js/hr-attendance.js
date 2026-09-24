// حاضری روزانه: دکمهٔ «ثبت‌نشده‌ها = حاضر» فقط ردیف‌هایی را پر می‌کند که هنوز وضعیت ندارند؛
// ردیفِ ثبت‌شده و روزِ رخصتی دست نمی‌خورد. ذخیره همچنان با دکمهٔ فرم است.
(function () {
    var root = document.querySelector('[data-hr-attendance]');
    if (!root) return;

    root.addEventListener('click', function (event) {
        var button = event.target.closest('[data-hr-mark-all]');
        if (!button) return;

        var value = button.getAttribute('data-hr-mark-all');
        root.querySelectorAll('select[data-hr-status]').forEach(function (select) {
            if (!select.disabled && select.value === '') {
                select.value = value;
            }
        });
    });
})();
