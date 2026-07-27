(function () {
    "use strict";

    function initialize(form) {
        if (form.dataset.customsExcelReady === "true") return;
        form.dataset.customsExcelReady = "true";
        var component = form.querySelector("[data-excel-import]");
        if (!component) return;

        var previewUrl = form.getAttribute("data-preview-url");
        var saveUrl = form.getAttribute("data-save-url") || form.action;

        async function post(url) {
            var response = await fetch(url, {
                method: "POST",
                body: new FormData(form),
                headers: { "X-Requested-With": "XMLHttpRequest" },
                credentials: "same-origin"
            });
            var data = await response.json().catch(function () { return {}; });
            if (!response.ok || !data.ok) throw new Error(data.message || "عملیات Import انجام نشد.");
            return data;
        }

        function previewRows(rows) {
            return (rows || []).slice(0, 10).map(function (row) {
                return {
                    row: row.rowNumber,
                    values: {
                        "سیمیر": row.simir || "—",
                        "نمبر موتر": row.plate || "—",
                        "وزن": row.weight == null ? "—" : row.weight,
                        "مجموع AFN": row.afn == null ? "—" : row.afn,
                        "وضعیت": row.matched ? "قابل ثبت" : (row.skip || "رد می‌شود")
                    }
                };
            });
        }

        function issues(rows) {
            return (rows || []).filter(function (row) { return !row.matched; }).slice(0, 200).map(function (row) {
                return { row: row.rowNumber, column: "تطبیق عملیات", message: row.skip || "عملیات در جریان پیدا نشد.", severity: "error" };
            });
        }

        component.addEventListener("excel-import:start", async function (event) {
            var api = event.detail;
            var rate = Number.parseFloat(form.querySelector('[name="FxRateToUsd"]')?.value || "0");
            if (!(rate > 0)) return api.fail("نرخ تبدیل افغانی به دالر را وارد کنید.");
            api.progress({ stage: 2, message: "در حال خواندن فایل و تطبیق با عملیات‌های در جریان…" });
            try {
                var data = await post(previewUrl);
                var rows = data.rows || [];
                api.ready({
                    totalRows: rows.length,
                    validRows: data.savedCount || 0,
                    warningRows: 0,
                    errorRows: data.skippedCount || 0,
                    duplicateRows: 0,
                    previewRows: previewRows(rows),
                    issues: issues(rows)
                });
            } catch (error) {
                api.fail(error.message);
            }
        });

        component.addEventListener("excel-import:confirm", async function (event) {
            var api = event.detail;
            api.progress({ stage: 4, state: 3, message: "در حال ثبت نهایی اعلامیه‌های معتبر داخل Transaction…" });
            try {
                var data = await post(saveUrl);
                api.complete({
                    message: "اعلامیه‌های گمرکی معتبر با موفقیت ثبت شدند.",
                    createdRows: data.savedCount || 0,
                    rejectedRows: data.skippedCount || 0,
                    updatedRows: 0,
                    redirectUrl: "/CustomsDeclarations"
                });
            } catch (error) {
                api.fail(error.message);
            }
        });

        form.addEventListener("submit", function (event) { event.preventDefault(); });
        form.addEventListener("change", function (event) {
            if (event.target.matches('input[name="Scope"], input[name="FxRateToUsd"]')) {
                component.dispatchEvent(new CustomEvent("excel-import:reset-request"));
            }
        });
    }

    function boot(scope) {
        var host = scope && typeof scope.querySelectorAll === "function" ? scope : document;
        host.querySelectorAll("[data-customs-import]").forEach(initialize);
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", function () { boot(document); }, { once: true });
    else boot(document);
    window.addEventListener("ptg:page-ready", function () { boot(document); });
    document.addEventListener("ptg:page-loaded", function (event) { boot(event.target); });
})();
