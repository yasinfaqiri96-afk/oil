/*
 * PTG — صفحهٔ پشتیبان‌گیری.
 *
 * چرا AJAX و نه ارسال معمولی فرم:
 * ساخت بکاپ در پس‌زمینه اجرا می‌شود، پس پاسخ POST هیچ‌وقت «نتیجهٔ بکاپ» نیست.
 * با ارسال معمولی، محافظِ ضدارسالِ دوباره دکمه را قفل می‌کرد و صفحهٔ تازه هم آن را
 * دوباره غیرفعال تحویل می‌داد؛ نتیجه یک دکمهٔ همیشه‌قفل بدون هیچ پیام یا خطا بود.
 * اینجا دکمه فقط تا لحظهٔ «ثبت درخواست» قفل است و وضعیت واقعی با Poll به‌روز می‌شود.
 *
 * UI only. هیچ منطق بکاپ، Drive یا نگهداری اینجا نیست.
 */
(function () {
    "use strict";

    var POLL_INTERVAL_MS = 3000;
    var POLL_MAX_MS = 30 * 60 * 1000; // سقف ایمنی تا Poll تا ابد نچرخد
    var pollTimer = null;
    var pollStartedAt = 0;
    var lastSeenRunId = null;

    function isEnglish() {
        return document.documentElement.lang === "en";
    }

    function t(fa, en) {
        return isEnglish() ? en : fa;
    }

    function notify(type, message) {
        if (typeof window.ptgToast === "function") {
            window.ptgToast(type, message);
            return;
        }
        // اگر ماژول توست در دسترس نبود، پیام نباید بی‌صدا گم شود.
        window.alert(message);
    }

    function liveRegion() {
        return document.querySelector("[data-backup-live]");
    }

    function createButton() {
        return document.querySelector("[data-backup-create]");
    }

    function setButtonBusy(button, busy) {
        if (!button) { return; }
        var label = button.querySelector("[data-create-label]");
        button.disabled = busy;
        if (!label) { return; }
        label.textContent = busy
            ? (button.getAttribute("data-busy-text") || t("در حال اجرا…", "Running…"))
            : (button.getAttribute("data-idle-text") || t("ایجاد بکاپ اکنون", "Create backup now"));
    }

    function antiForgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    /// آدرس‌ها از خود صفحه خوانده می‌شوند تا زیر PathBase هم درست بمانند.
    function endpoint(name, fallback) {
        var page = document.querySelector("[data-backups-page]");
        return (page && page.getAttribute("data-" + name + "-url")) || fallback;
    }

    // ---- Poll ---------------------------------------------------------------

    function stopPolling() {
        if (pollTimer) {
            window.clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    function startPolling() {
        stopPolling();
        pollStartedAt = Date.now();
        pollTimer = window.setTimeout(pollOnce, POLL_INTERVAL_MS);
    }

    function pollOnce() {
        pollTimer = null;

        fetch(endpoint("status", "/Backups/Status"), {
            method: "GET",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            credentials: "same-origin"
        })
            .then(function (response) {
                if (!response.ok) { throw new Error("HTTP " + response.status); }
                return response.text();
            })
            .then(function (html) {
                var region = liveRegion();
                if (!region) { return; }

                var holder = document.createElement("div");
                holder.innerHTML = html;
                var fresh = holder.querySelector("[data-backup-live]");
                if (!fresh) { return; }

                region.replaceWith(fresh);
                afterLiveRegionSwap(fresh);
            })
            .catch(function (error) {
                // شکست یک Poll نباید حلقه را بکشد؛ دفعهٔ بعد دوباره تلاش می‌شود.
                stopPolling();
                setButtonBusy(createButton(), false);
                notify("error", t("به‌روزرسانی وضعیت بکاپ ناموفق بود: ", "Refreshing the backup status failed: ") + error.message);
            });
    }

    function afterLiveRegionSwap(region) {
        var busy = region.getAttribute("data-running") === "true"
            || region.getAttribute("data-queued") === "true";

        if (busy) {
            if (Date.now() - pollStartedAt > POLL_MAX_MS) {
                stopPolling();
                setButtonBusy(createButton(), false);
                notify("warning", t(
                    "بکاپ هنوز در حال اجراست. صفحه را بعداً دوباره باز کنید.",
                    "The backup is still running. Re-open this page later."));
                return;
            }

            setButtonBusy(createButton(), true);
            pollTimer = window.setTimeout(pollOnce, POLL_INTERVAL_MS);
            return;
        }

        // اجرا تمام شد: Poll متوقف، دکمه آزاد و نتیجه اعلام می‌شود.
        stopPolling();
        setButtonBusy(createButton(), false);
        announceResult(region);
    }

    function announceResult(region) {
        var status = region.getAttribute("data-latest-status") || "";
        var runId = region.getAttribute("data-latest-id") || "";
        if (!runId || runId === lastSeenRunId) { return; }
        lastSeenRunId = runId;

        if (status === "Succeeded") {
            notify("success", t("بکاپ با موفقیت ساخته شد.", "The backup was created successfully."));
            return;
        }

        if (status === "Failed") {
            var error = region.getAttribute("data-latest-error") || "";
            notify("error", t("بکاپ ناموفق بود: ", "The backup failed: ") + (error || t("علت نامشخص", "unknown reason")));
        }
    }

    // ---- Create backup ------------------------------------------------------

    function requestBackup(button) {
        setButtonBusy(button, true);

        // آیا پس از پایان درخواست باید در حالت انتظار بماند؟ فقط وقتی بکاپی واقعاً
        // شروع شده باشد. در همهٔ مسیرهای دیگر — موفق، خطا یا رد شدن — دکمه آزاد می‌شود.
        var keepBusy = false;

        var body = new URLSearchParams();
        body.append("__RequestVerificationToken", antiForgeryToken());

        fetch(endpoint("create", "/Backups/CreateNow"), {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded",
                "X-Requested-With": "XMLHttpRequest"
            },
            credentials: "same-origin",
            body: body.toString()
        })
            .then(function (response) {
                if (!response.ok) { throw new Error("HTTP " + response.status); }
                return response.json();
            })
            .then(function (result) {
                if (result.queued) {
                    notify("success", result.message);
                } else if (result.isRunning) {
                    notify("warning", result.message || t("یک بکاپ در حال اجرا است.", "A backup is already running."));
                } else {
                    notify("warning", result.message || t("درخواست بکاپ ثبت نشد.", "The backup request was not accepted."));
                }

                if (result.shouldPoll) {
                    keepBusy = true;
                    stopPolling();
                    pollStartedAt = Date.now();
                    // Poll اول کمی زودتر تا ردیف «در حال اجرا» سریع دیده شود.
                    pollTimer = window.setTimeout(pollOnce, 800);
                }
            })
            .catch(function (error) {
                keepBusy = false;
                notify("error", t("ثبت درخواست بکاپ ناموفق بود: ", "Submitting the backup request failed: ") + error.message);
            })
            .finally(function () {
                // در هیچ مسیری دکمه بی‌نهایت قفل نمی‌ماند: یا همین‌جا آزاد می‌شود،
                // یا Poll در پایان اجرا آزادش می‌کند.
                if (!keepBusy) {
                    setButtonBusy(createButton(), false);
                }
            });
    }

    // ---- Error modal --------------------------------------------------------

    function openErrorModal(trigger) {
        var modalEl = document.getElementById("backup-error-modal");
        if (!modalEl) { return; }

        var subject = modalEl.querySelector("[data-backup-error-subject]");
        var text = modalEl.querySelector("[data-backup-error-text]");
        if (subject) { subject.textContent = trigger.getAttribute("data-backup-error-title") || ""; }
        if (text) { text.textContent = trigger.getAttribute("data-backup-error") || ""; }

        if (window.bootstrap && window.bootstrap.Modal) {
            window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
        }
    }

    // ---- Google Drive edit toggle ------------------------------------------

    function toggleDriveForm() {
        var form = document.querySelector("[data-drive-form]");
        if (!form) { return; }
        form.hidden = !form.hidden;
        if (!form.hidden) {
            var first = form.querySelector("input");
            if (first) { first.focus(); }
        }
    }

    // ---- Wiring -------------------------------------------------------------

    function onClick(event) {
        var createBtn = event.target.closest("[data-backup-create]");
        if (createBtn) {
            event.preventDefault();
            if (createBtn.disabled) { return; }
            requestBackup(createBtn);
            return;
        }

        var errorBtn = event.target.closest("[data-backup-error]");
        if (errorBtn) {
            event.preventDefault();
            openErrorModal(errorBtn);
            return;
        }

        if (event.target.closest("[data-drive-edit-toggle]")) {
            event.preventDefault();
            toggleDriveForm();
        }
    }

    var wired = false;

    function init() {
        if (!document.querySelector("[data-backups-page]")) {
            stopPolling();
            return;
        }

        if (!wired) {
            wired = true;
            document.addEventListener("click", onClick);
        }

        var region = liveRegion();
        if (!region) { return; }

        lastSeenRunId = region.getAttribute("data-latest-id") || null;

        // اگر صفحه وسط یک اجرا باز شد، بدون کلیک هم وضعیت را دنبال کن.
        if (region.getAttribute("data-running") === "true" || region.getAttribute("data-queued") === "true") {
            setButtonBusy(createButton(), true);
            startPolling();
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }

    // ناوبری SPA فقط <main> را عوض می‌کند؛ باید دوباره راه بیفتد.
    window.addEventListener("ptg:page-ready", init);
    window.PTG = window.PTG || {};
    window.PTG.initializeBackupsPage = init;
})();
