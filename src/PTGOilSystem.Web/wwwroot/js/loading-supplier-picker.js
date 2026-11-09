// فرم ثبت بارگیری: فیلد اختیاری «تأمین‌کننده» فقط برای قرارداد چندتأمین‌کننده دیده می‌شود و فقط
// تأمین‌کننده‌های همان قرارداد(ها) را نشان می‌دهد.
(function () {
    "use strict";

    function boot() {
        var field = document.querySelector("[data-loading-supplier-field]");
        var select = field ? field.querySelector("[data-loading-supplier-select]") : null;
        if (!field || !select) {
            return;
        }

        function sync() {
            var multiIds = Array.from(document.querySelectorAll("[data-loading-contract-checkbox]"))
                .filter(function (box) { return box.checked && box.dataset.contractMultiSupplier === "true"; })
                .map(function (box) { return box.value; });

            var show = multiIds.length > 0;
            field.hidden = !show;
            select.disabled = !show;
            // اختیاری: بارگیری بدون تأمین‌کننده به خودِ قرارداد تعلق دارد و طلب به نسبت سهم تقسیم می‌شود.
            select.required = false;

            Array.from(select.options).forEach(function (option) {
                if (!option.value) {
                    return;
                }

                var contracts = (option.dataset.contractIds || "").split(" ");
                var allowed = multiIds.every(function (id) { return contracts.indexOf(id) >= 0; });
                option.hidden = !allowed;
                option.disabled = !allowed;
            });

            var selected = select.options[select.selectedIndex];
            if (!show || (selected && selected.disabled)) {
                select.value = "";
            }
        }

        document.addEventListener("change", function (event) {
            if (event.target && event.target.matches && event.target.matches("[data-loading-contract-checkbox]")) {
                sync();
            }
        });
        sync();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot);
    } else {
        boot();
    }
})();
