/*
 * PTG — «راهنمای هوشمند».
 *
 * فقط UI است: پنل چت را باز/بسته می‌کند، ساختار صفحهٔ فعلی را از DOM می‌خواند
 * و همراه سؤال به Backend می‌فرستد. هیچ منطق تجاری، مالی یا داده‌ای اینجا نیست
 * و کلید API هرگز به این لایه نمی‌رسد؛ تماس با Claude فقط در Backend انجام می‌شود.
 *
 * چرا Context در لحظهٔ ارسال خوانده می‌شود و نه یک‌بار در startup:
 * پوستهٔ برنامه با ناوبری SPA باقی می‌ماند و فقط <main> عوض می‌شود، پس
 * فیلدها و دکمه‌ها باید در همان لحظهٔ پرسیدن از صفحهٔ جاری خوانده شوند.
 */
(function () {
    "use strict";

    var MAX_FIELDS = 40;
    var MAX_BUTTONS = 15;
    var MAX_ERROR_LENGTH = 300;
    var MAX_LABEL_LENGTH = 60;

    var root = null;
    var panel = null;
    var log = null;
    var input = null;
    var sendButton = null;
    var spinner = null;
    var busy = false;
    var lastFocusedFieldLabel = "";

    function isEnglish() {
        return document.documentElement.lang === "en";
    }

    function t(fa, en) {
        return isEnglish() ? en : fa;
    }

    function clean(value, maxLength) {
        if (!value) { return ""; }
        var text = String(value).replace(/\s+/g, " ").trim();
        return text.length > maxLength ? text.slice(0, maxLength) : text;
    }

    function antiForgeryToken() {
        var field = root && root.querySelector('input[name="__RequestVerificationToken"]');
        if (field) { return field.value; }
        var fallback = document.querySelector('input[name="__RequestVerificationToken"]');
        return fallback ? fallback.value : "";
    }

    // ---- خواندن Context صفحه ------------------------------------------------

    /// controller/action از کلاس بدنه که _Layout روی <body> می‌گذارد خوانده می‌شود.
    function routeFromBodyClass(prefix) {
        var match = /(?:^|\s)controller-([a-z0-9]+)/.exec(document.body.className);
        var actionMatch = /(?:^|\s)action-([a-z0-9]+)/.exec(document.body.className);
        return prefix === "controller"
            ? (match ? match[1] : "")
            : (actionMatch ? actionMatch[1] : "");
    }

    function pageTitle() {
        var main = document.querySelector("main");
        var heading = main && main.querySelector("h1, .page-title, .ak-page-title");
        if (heading) { return clean(heading.textContent, 150); }
        return clean(document.title, 150);
    }

    /// برچسب یک کنترل: label مرتبط، سپس aria-label، سپس placeholder، سپس name.
    function controlLabel(control) {
        var id = control.getAttribute("id");
        if (id) {
            var labelled = document.querySelector('label[for="' + (window.CSS && CSS.escape ? CSS.escape(id) : id) + '"]');
            if (labelled) { return clean(labelled.textContent, MAX_LABEL_LENGTH); }
        }
        var wrapping = control.closest("label");
        if (wrapping) { return clean(wrapping.textContent, MAX_LABEL_LENGTH); }
        return clean(
            control.getAttribute("aria-label")
                || control.getAttribute("placeholder")
                || control.getAttribute("name"),
            MAX_LABEL_LENGTH);
    }

    function collectFields() {
        var main = document.querySelector("main");
        if (!main) { return []; }
        var controls = main.querySelectorAll("input:not([type=hidden]):not([type=submit]):not([type=button]), select, textarea");
        var seen = Object.create(null);
        var fields = [];
        for (var i = 0; i < controls.length && fields.length < MAX_FIELDS; i += 1) {
            var control = controls[i];
            if (control.type === "search") { continue; }
            var label = controlLabel(control);
            if (!label || seen[label]) { continue; }
            var required = control.required || control.getAttribute("aria-required") === "true";
            seen[label] = true;
            fields.push(required ? label + " (الزامی)" : label);
        }
        return fields;
    }

    function collectButtons() {
        var main = document.querySelector("main");
        if (!main) { return []; }
        var nodes = main.querySelectorAll("button, a.btn, input[type=submit], .ak-btn");
        var seen = Object.create(null);
        var buttons = [];
        for (var i = 0; i < nodes.length && buttons.length < MAX_BUTTONS; i += 1) {
            var node = nodes[i];
            var text = clean(node.value || node.textContent || node.getAttribute("aria-label"), 40);
            if (!text || seen[text]) { continue; }
            seen[text] = true;
            buttons.push(text);
        }
        return buttons;
    }

    function collectError() {
        var selectors = [
            ".validation-summary-errors",
            ".field-validation-error",
            ".alert-danger",
            "[data-toast-error]"
        ];
        for (var i = 0; i < selectors.length; i += 1) {
            var node = document.querySelector(selectors[i]);
            if (node && clean(node.textContent, MAX_ERROR_LENGTH)) {
                return clean(node.textContent, MAX_ERROR_LENGTH);
            }
        }
        return "";
    }

    function buildContext() {
        return {
            // مسیر با Query فرستاده می‌شود چون شناسهٔ رکورد گاهی فقط آنجاست
            // (مثل History?supplierId=4). Backend فقط یک عدد از آن بیرون می‌کشد.
            route: clean(window.location.pathname + window.location.search, 200),
            controller: routeFromBodyClass("controller"),
            action: routeFromBodyClass("action"),
            pageTitle: pageTitle(),
            fields: collectFields(),
            buttons: collectButtons(),
            errorMessage: collectError(),
            focusedField: lastFocusedFieldLabel
        };
    }

    // ---- حافظهٔ گفتگو -------------------------------------------------------
    // تاریخچه فقط در همین صفحه و در حافظهٔ مرورگر می‌ماند؛ ذخیره نمی‌شود و با
    // بارگذاری دوباره پاک می‌گردد. Backend خودش تعداد و طول را دوباره محدود می‌کند.
    var history = [];
    var MAX_HISTORY_MESSAGES = 12;

    function rememberTurn(role, content) {
        if (!content) { return; }
        history.push({ role: role, content: String(content).slice(0, 1200) });
        if (history.length > MAX_HISTORY_MESSAGES) {
            history = history.slice(history.length - MAX_HISTORY_MESSAGES);
        }
    }

    // ---- رندر گفتگو ---------------------------------------------------------

    function appendMessage(text, kind) {
        if (!log) { return; }
        var wrapper = document.createElement("div");
        wrapper.className = "ptg-assistant-msg is-" + kind;
        var paragraph = document.createElement("p");
        paragraph.textContent = text;
        wrapper.appendChild(paragraph);
        log.appendChild(wrapper);
        log.scrollTop = log.scrollHeight;
        return wrapper;
    }

    // نام ابزارها به زبان کاربر، تا معلوم باشد پاسخ از کدام داده آمده است.
    var TOOL_LABELS = {
        search_party: { fa: "فهرست اشخاص", en: "party list" },
        get_party_balance: { fa: "مانده حساب", en: "account balance" },
        get_stock_balance: { fa: "موجودی انبار", en: "stock balance" },
        get_contracts: { fa: "قراردادها", en: "contracts" },
        get_loading_details: { fa: "پروندهٔ بارگیری", en: "loading file" },
        get_contract_progress: { fa: "پیشرفت قرارداد", en: "contract progress" },
        get_open_contracts: { fa: "قراردادهای باز", en: "open contracts" },
        get_party_ledger: { fa: "صورتحساب شخص", en: "party ledger" }
    };

    function appendSources(usedTools) {
        if (!log || !usedTools || !usedTools.length) { return; }

        var names = [];
        for (var i = 0; i < usedTools.length; i += 1) {
            var label = TOOL_LABELS[usedTools[i]];
            names.push(label ? t(label.fa, label.en) : usedTools[i]);
        }

        var note = document.createElement("div");
        note.className = "ptg-assistant-source";
        note.textContent = t("منبع: ", "Source: ") + names.join("، ");
        log.appendChild(note);
        log.scrollTop = log.scrollHeight;
    }

    function setBusy(value) {
        busy = value;
        if (sendButton) { sendButton.disabled = value; }
        if (spinner) { spinner.hidden = !value; }
        if (input) { input.readOnly = value; }
    }

    // ---- ارسال --------------------------------------------------------------

    function ask(question) {
        if (busy || !question) { return; }
        // سؤال‌های آماده جای‌شان را به متن راهنما می‌دهند تا پاسخ کامل دیده شود.
        if (root) { root.classList.add("has-conversation"); }
        appendMessage(question, "user");
        if (input) { input.value = ""; }
        setBusy(true);

        var thinking = appendMessage(t("در حال بررسی...", "Thinking..."), "bot");
        var url = root.getAttribute("data-assistant-url") || "/Assistant/Ask";

        fetch(url, {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": antiForgeryToken()
            },
            // تاریخچه پیش از افزودن سؤال جاری برداشته می‌شود تا سؤال دو بار نرود.
            body: JSON.stringify({
                question: question,
                context: buildContext(),
                history: history.slice()
            })
        })
            .then(function (response) {
                if (!response.ok) { throw new Error("HTTP " + response.status); }
                return response.json();
            })
            .then(function (payload) {
                if (thinking) { thinking.remove(); }
                var message = (payload && payload.message)
                    || t("پاسخی دریافت نشد.", "No answer received.");
                var ok = !!(payload && payload.ok);
                appendMessage(message, ok ? "bot" : "error");

                // فقط نوبت موفق در حافظه می‌ماند؛ پیام خطا زمینهٔ گفتگو را خراب می‌کند.
                if (ok) {
                    rememberTurn("user", question);
                    rememberTurn("assistant", message);
                    appendSources(payload && payload.usedTools);
                }
            })
            .catch(function () {
                if (thinking) { thinking.remove(); }
                appendMessage(
                    t("ارتباط با دستیار برقرار نشد. لطفاً دوباره تلاش کنید.",
                        "Could not reach the assistant. Please try again."),
                    "error");
            })
            .then(function () {
                setBusy(false);
                if (input) { input.focus(); }
            });
    }

    // ---- باز/بسته ----------------------------------------------------------

    function setOpen(open) {
        if (!panel || !root) { return; }
        panel.hidden = !open;
        root.classList.toggle("is-open", open);
        var trigger = root.querySelector("[data-assistant-open]");
        if (trigger) { trigger.setAttribute("aria-expanded", open ? "true" : "false"); }
        if (open && input) { input.focus(); }
    }

    // ---- اتصال --------------------------------------------------------------

    function init() {
        root = document.querySelector("[data-assistant-root]");
        if (!root) { return; }

        panel = root.querySelector("[data-assistant-panel], .ptg-assistant-panel");
        log = root.querySelector("[data-assistant-log]");
        input = root.querySelector("[data-assistant-input]");
        sendButton = root.querySelector("[data-assistant-send]");
        spinner = root.querySelector("[data-assistant-spinner]");

        root.addEventListener("click", function (event) {
            var target = event.target;
            if (target.closest("[data-assistant-open]")) {
                setOpen(panel.hidden);
                return;
            }
            if (target.closest("[data-assistant-close]")) {
                setOpen(false);
                return;
            }
            var suggestion = target.closest("[data-assistant-suggestion]");
            if (suggestion) {
                ask(clean(suggestion.textContent, 500));
            }
        });

        var form = root.querySelector("[data-assistant-form]");
        if (form) {
            form.addEventListener("submit", function (event) {
                event.preventDefault();
                ask(clean(input && input.value, 500));
            });
        }

        if (input) {
            input.addEventListener("keydown", function (event) {
                if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    ask(clean(input.value, 500));
                }
            });
        }

        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && panel && !panel.hidden) { setOpen(false); }
        });

        /// آخرین فیلدی که کاربر روی آن بوده، برای سؤال «این فیلد برای چیست؟».
        document.addEventListener("focusin", function (event) {
            var control = event.target;
            if (!control || !control.matches || !control.matches("input, select, textarea")) { return; }
            if (root.contains(control)) { return; }
            lastFocusedFieldLabel = controlLabel(control);
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
