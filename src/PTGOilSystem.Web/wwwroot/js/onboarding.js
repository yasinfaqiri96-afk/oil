/*
 * راهنمای شروع — کارت داشبورد (بستن/بازکردن) و تورهای کوتاه روی صفحه‌های واقعی.
 * فقط وقتی Onboarding:Enabled روشن است بارگذاری می‌شود و هیچ داده‌ای را تغییر نمی‌دهد.
 *
 * وضعیت:
 *   - کوکی <key>            کارت‌های بسته‌شده ("setup" / "tasks") تا سرور همان‌جا رندرشان نکند.
 *   - localStorage <key>:tours   تورهایی که کاربر تا آخر دیده است (فقط برای تیک کنار کار).
 *   - sessionStorage ptg-tour     توری که در جریان است، تا با ناوبری SPA یا رفرش ادامه یابد.
 * کلید از data-onboarding-key کارت داشبورد می‌آید و برای هر کاربر جداست؛ هر Trial دامنهٔ خودش
 * را دارد، پس چیزی بین Tenantها مشترک نمی‌شود.
 */
(function () {
    "use strict";

    if (window.__ptgOnboardingReady === true) {
        return;
    }
    window.__ptgOnboardingReady = true;

    var STATE_KEY = "ptg-tour";
    var PAD = 6;
    var GAP = 12;
    var EDGE = 16;
    var WAIT_MS = 1500;

    function nav(controller) {
        return '.ptg-sidebar-nav [data-nav-item="' + controller + '"] > a';
    }

    // دکمهٔ «جدید» سرصفحه؛ list-toolbar-row.js گاهی آن را به نوار ابزار جدول می‌برد.
    // در موبایل/تبلت منوی کناری بسته است؛ به‌جای حفره، راه باز کردن منو گفته می‌شود.
    function navHint(fa, en) {
        return ["از دکمهٔ ☰ بالای صفحه منو را باز کنید و «" + fa + "» را بزنید؛ یا «بعدی» را بزنید تا خودمان ببریم.",
            "Open the ☰ menu at the top and choose “" + en + "”, or click Next and we will take you there."];
    }

    var PRIMARY = ".ptg-page .ak-primary-action:not(.ak-empty-action)";
    var SAVE = "form .ak-save";

    // هر قدم: at = "controller/action" یا "*"؛ go = آدرس وقتی کاربر در صفحهٔ دیگری است؛
    // click = «بعدی» همان عنصر را می‌زند تا کاربر مسیر واقعی را طی کند.
    var TOURS = {
        owner: [
            {
                at: "companies/index", go: "/Companies", target: ".ak-table tbody tr .ak-check", tick: true,
                title: ["شرکت خود را انتخاب کنید", "Select your company"],
                text: ["تیک کنار نام شرکت خود را بزنید.", "Tick the box next to your company."],
                missing: ["هنوز شرکتی ثبت نشده است. اول از داشبورد شرکت خود را ثبت کنید.",
                    "No company yet. Register your company from the dashboard first."]
            },
            {
                at: "companies/index", go: "/Companies",
                target: '.ak-bulk-actions .ak-row-action[aria-label="تعیین به‌عنوان مالک سیستم"], .ak-bulk-actions .ak-row-action[aria-label="Set as system owner"]',
                title: ["مالک سیستم", "System owner"],
                text: ["روی ستاره «تعیین به‌عنوان مالک سیستم» بزنید و تأیید کنید. بعد به داشبورد برگردید؛ این قدم خودش تیک می‌خورد.",
                    "Click the star “Set as system owner” and confirm. Then return to the dashboard; the step ticks itself."],
                missing: ["اول تیک کنار شرکت خود را بزنید تا دکمهٔ ستاره ظاهر شود.",
                    "First tick your company so the star button appears."]
            }
        ],
        contract: [
            {
                at: "*", target: nav("Contracts"), click: true, missing: navHint("قراردادها", "Contracts"),
                title: ["بخش قراردادها", "Contracts"],
                text: ["هر خرید و فروش از یک قرارداد شروع می‌شود. قراردادها را از این منو باز کنید.",
                    "Every purchase and sale starts with a contract. Open contracts from this menu."]
            },
            {
                at: "contracts/index", go: "/Contracts", target: PRIMARY, click: true,
                title: ["قرارداد تازه", "New contract"],
                text: ["این دکمه فرم ثبت قرارداد را باز می‌کند.", "This button opens the contract form."]
            },
            {
                at: "contracts/create", go: "/Contracts/Create", target: "#ProductId", closest: ".ak-field",
                title: ["جنس، فروشنده و مقدار", "Product, supplier and quantity"],
                text: ["محصول، تأمین‌کننده، مقدار و قیمت را وارد کنید. خانه‌های ستاره‌دار (*) لازم است.",
                    "Enter product, supplier, quantity and price. Fields marked * are required."]
            },
            {
                at: "contracts/create", go: "/Contracts/Create", target: SAVE,
                title: ["ذخیره", "Save"],
                text: ["بعد از ذخیره، روی همین قرارداد بارگیری ثبت می‌کنید.",
                    "After saving, you record loadings against this contract."]
            }
        ],
        loading: [
            {
                at: "*", target: nav("Loading"), click: true, missing: navHint("عملیات", "Operations"),
                title: ["بخش عملیات", "Operations"],
                text: ["بارگیری، رسید، حمل و فروش همه در «عملیات» است.",
                    "Loadings, receipts, transport and sales are all under Operations."]
            },
            {
                at: "loading/index", go: "/Loading", target: PRIMARY, click: true,
                title: ["ثبت بارگیری", "New loading"],
                text: ["هر بار که جنس بار می‌شود، از این دکمه یک بارگیری ثبت کنید.",
                    "Each time goods are loaded, record a loading with this button."]
            },
            {
                at: "loading/create", go: "/Loading/Create", target: "[data-loading-contract-field]", closest: ".ak-field",
                title: ["قرارداد را انتخاب کنید", "Choose the contract"],
                text: ["هر بارگیری به یک قرارداد وصل است و جنس خودش از قرارداد می‌آید. بعد مقدار و تاریخ را وارد کنید.",
                    "Each loading belongs to a contract; the product comes from it. Then enter quantity and date."]
            },
            {
                at: "loading/create", go: "/Loading/Create", target: SAVE,
                title: ["ذخیره، بعد رسید", "Save, then receipt"],
                text: ["وقتی بار به مقصد رسید، همین بارگیری را از فهرست باز کنید و «ثبت رسید» را بزنید تا به موجودی اضافه شود. برای رسید، یک ترمینال در «تعاریف پایه» لازم است.",
                    "When the goods arrive, open this loading and click “Register receipt” to add it to stock. A receipt needs a terminal under Base Definitions."]
            }
        ],
        payment: [
            {
                at: "*", target: nav("Finance"), click: true, missing: navHint("مالی", "Finance"),
                title: ["بخش مالی", "Finance"],
                text: ["دریافت و پرداخت پول، صندوق‌ها و حساب‌ها در «مالی» است.",
                    "Money in and out, cash boxes and accounts are under Finance."]
            },
            {
                at: "finance/index", go: "/Finance", target: '.ptg-page a[href$="/Payments"]', click: true,
                title: ["روزنامچه", "Journal"],
                text: ["همهٔ پول‌هایی که گرفته‌اید یا داده‌اید اینجا ثبت می‌شود.",
                    "Every amount you received or paid is recorded here."]
            },
            {
                at: "payments/index", go: "/Payments", target: PRIMARY, click: true,
                title: ["ثبت دریافت یا پرداخت", "Record money in or out"],
                text: ["این دکمه فرم ثبت پول را باز می‌کند.", "This button opens the money form."]
            },
            {
                at: "payments/create", go: "/Payments/Create", target: "#journalDirectionGroup",
                title: ["پول آمد یا رفت؟", "Money in or out?"],
                text: ["اول مشخص کنید پول دریافت کرده‌اید یا پرداخت.", "First choose whether you received or paid money."]
            },
            {
                at: "payments/create", go: "/Payments/Create", target: "#cashAccountSelect", closest: ".ak-field",
                title: ["صندوق یا بانک", "Cash box or bank"],
                text: ["پول از کدام صندوق یا بانک؟ اگر فهرست خالی است، اول در «مالی» یک حساب نقد/بانک بسازید. بعد طرف حساب و مبلغ را وارد و ذخیره کنید.",
                    "Which cash box or bank? If the list is empty, create a cash/bank account under Finance first. Then enter the party and amount and save."]
            }
        ],
        statement: [
            {
                at: "*", target: nav("Suppliers"), click: true, missing: navHint("طرف حساب‌ها", "Parties"),
                title: ["طرف حساب‌ها", "Parties"],
                text: ["تأمین‌کنندگان، مشتریان و دیگر طرف‌ها اینجا هستند.",
                    "Suppliers, customers and other parties are here."]
            },
            {
                at: "suppliers/index", go: "/Suppliers", target: ".ak-table tbody .ak-name", click: true,
                title: ["شخص را انتخاب کنید", "Choose the person"],
                text: ["روی نام تأمین‌کننده بزنید. برای مشتری، همین کار را در تب «مشتریان» انجام دهید.",
                    "Click the supplier's name. For a customer, do the same on the Customers tab."],
                missing: ["هنوز تأمین‌کننده‌ای ثبت نشده است. اول یکی ثبت کنید، بعد صورت‌حسابش را ببینید.",
                    "No supplier yet. Add one first, then view its statement."]
            },
            {
                at: "suppliers/details", target: '[data-details-tabs] a[href*="tab=statement"]', click: true,
                title: ["صورت‌حساب", "Statement"],
                text: ["این تب همهٔ بارگیری‌ها، پرداخت‌ها و ماندهٔ حساب را نشان می‌دهد.",
                    "This tab shows every loading, payment and the balance."]
            },
            {
                at: "suppliers/details", target: ".statement-screen-header, .statement-screen-tools",
                title: ["بازهٔ تاریخ و خروجی", "Date range and export"],
                text: ["برای یک دورهٔ خاص، در همین خانهٔ جستجو «بازه تاریخ» را انتخاب کنید. خروجی Excel و PDF هم همین‌جاست.",
                    "For a specific period, choose “Date range” in this search box. Excel and PDF export are here too."]
            }
        ],
        inventory: [
            {
                at: "*", target: nav("Reports"), click: true, missing: navHint("گزارش‌ها", "Reports"),
                title: ["گزارش‌ها", "Reports"],
                text: ["گزارش موجودی، مالی و مفاد همه در این بخش است.",
                    "Stock, finance and profit reports are all here."]
            },
            {
                at: "reports/index", go: "/Reports", target: 'a.rephub-card[href$="/Reports/InventoryOperations"]', click: true,
                title: ["موجودی و عملیات", "Inventory & operations"],
                text: ["مقدار جنس در هر ترمینال و مخزن، و گردش آن.",
                    "How much stock is in each terminal and tank, and how it moved."]
            },
            {
                at: "reports/inventoryoperations", go: "/Reports/InventoryOperations", target: ".ak-stat-grid",
                title: ["خلاصهٔ موجودی", "Stock summary"],
                text: ["موجودی با ثبت رسید بارگیری زیاد و با فروش کم می‌شود. اگر صفر است، هنوز رسیدی ثبت نشده.",
                    "Stock grows with loading receipts and falls with sales. If it is zero, no receipt is recorded yet."]
            }
        ],
        pnl: [
            {
                at: "*", target: nav("Reports"), click: true, missing: navHint("گزارش‌ها", "Reports"),
                title: ["گزارش‌ها", "Reports"],
                text: ["گزارش موجودی، مالی و مفاد همه در این بخش است.",
                    "Stock, finance and profit reports are all here."]
            },
            {
                at: "reports/index", go: "/Reports", target: 'a.rephub-card[href$="/Reports/ContractPnl"]', click: true,
                title: ["مفاد قراردادها", "Contract profit"],
                text: ["مفاد یا ضرر هر قرارداد خرید در یک جدول.", "Profit or loss of each purchase contract in one table."]
            },
            {
                at: "reports/contractpnl", go: "/Reports/ContractPnl", target: ".ak-stat-grid",
                title: ["مفاد هر قرارداد", "Profit per contract"],
                text: ["خرید، مصارف، فروش و مفاد هر قرارداد اینجاست. مفاد وقتی کامل است که فروش و مصارف ثبت شده باشد.",
                    "Purchase, costs, sales and profit per contract. Profit is complete once sales and costs are recorded."]
            }
        ]
    };

    // ---------- کمکی‌ها ----------
    function isEnglish() {
        return (document.documentElement.getAttribute("lang") || "").toLowerCase() === "en";
    }

    function tr(pair) {
        return pair ? (isEnglish() && pair[1] ? pair[1] : pair[0]) : "";
    }

    function digits(n) {
        var s = String(n);
        return isEnglish() ? s : s.replace(/\d/g, function (d) { return "۰۱۲۳۴۵۶۷۸۹".charAt(+d); });
    }

    function readJson(storage, key) {
        try {
            var raw = storage.getItem(key);
            return raw ? JSON.parse(raw) : null;
        } catch (_) {
            return null;
        }
    }

    function writeJson(storage, key, value) {
        try {
            if (value === null) {
                storage.removeItem(key);
            } else {
                storage.setItem(key, JSON.stringify(value));
            }
        } catch (_) { /* حالت خصوصی مرورگر: راهنما بدون حافظه هم کار می‌کند. */ }
    }

    function readCookie(name) {
        var parts = document.cookie ? document.cookie.split("; ") : [];
        for (var i = 0; i < parts.length; i++) {
            var eq = parts[i].indexOf("=");
            if (parts[i].slice(0, eq) === name) {
                return decodeURIComponent(parts[i].slice(eq + 1));
            }
        }
        return "";
    }

    function writeCookie(name, value) {
        var secure = location.protocol === "https:" ? "; Secure" : "";
        var age = value ? 31536000 : 0;
        document.cookie = name + "=" + encodeURIComponent(value) + "; path=/; max-age=" + age + "; SameSite=Lax" + secure;
    }

    function currentPage() {
        var cls = document.body ? document.body.className : "";
        var c = /(?:^|\s)controller-(\S+)/.exec(cls);
        var a = /(?:^|\s)action-(\S+)/.exec(cls);
        return (c ? c[1] : "") + "/" + (a ? a[1] : "");
    }

    function onPage(step) {
        return step.at === "*" || step.at === currentPage();
    }

    function navigate(url) {
        if (window.PTG && typeof window.PTG.spaNavigate === "function") {
            window.PTG.spaNavigate(url);
        } else {
            window.location.href = url;
        }
    }

    function reducedMotion() {
        return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }

    function isShown(el) {
        if (!el || !el.isConnected) {
            return false;
        }
        var r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) {
            return false;
        }
        // منوی کناری موبایل بیرون از صفحه است؛ آن را دیده‌نشده حساب کن.
        if (r.right <= 0 || r.left >= window.innerWidth) {
            return false;
        }
        var style = window.getComputedStyle(el);
        return style.visibility !== "hidden" && style.display !== "none";
    }

    function findTarget(step) {
        if (!step.target) {
            return null;
        }
        var list = document.querySelectorAll(step.target);
        for (var i = 0; i < list.length; i++) {
            var el = step.closest ? (list[i].closest(step.closest) || list[i]) : list[i];
            if (isShown(el)) {
                return el;
            }
        }
        return null;
    }

    // ---------- رابط تور ----------
    var ui = null;
    var activeTarget = null;
    var waitTimer = 0;
    var followTimer = 0;
    var frame = 0;

    function buildUi() {
        var root = document.createElement("div");
        root.className = "ptg-tour";
        root.setAttribute("data-tour-root", "");
        root.innerHTML =
            '<div class="ptg-tour-spot" aria-hidden="true"></div>' +
            '<div class="ptg-tour-card" role="dialog" aria-modal="false" aria-labelledby="ptgTourTitle" aria-describedby="ptgTourText">' +
            '  <div class="ptg-tour-top">' +
            '    <span class="ptg-tour-meta" data-tour-meta></span>' +
            '    <button type="button" class="btn-close" data-tour-skip></button>' +
            '  </div>' +
            '  <h2 class="ptg-tour-title" id="ptgTourTitle"></h2>' +
            '  <p class="ptg-tour-text" id="ptgTourText"></p>' +
            '  <div class="ptg-tour-actions">' +
            '    <button type="button" class="btn btn-light ak-secondary-action" data-tour-back></button>' +
            '    <button type="button" class="btn btn-primary ak-primary-action" data-tour-next></button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(root);

        root.querySelector("[data-tour-skip]").addEventListener("click", function () { end(false); });
        root.querySelector("[data-tour-back]").addEventListener("click", back);
        root.querySelector("[data-tour-next]").addEventListener("click", next);

        return {
            root: root,
            spot: root.querySelector(".ptg-tour-spot"),
            card: root.querySelector(".ptg-tour-card"),
            meta: root.querySelector("[data-tour-meta]"),
            title: root.querySelector(".ptg-tour-title"),
            text: root.querySelector(".ptg-tour-text"),
            back: root.querySelector("[data-tour-back]"),
            next: root.querySelector("[data-tour-next]"),
            skip: root.querySelector("[data-tour-skip]")
        };
    }

    function removeUi() {
        window.clearTimeout(waitTimer);
        window.clearInterval(followTimer);
        followTimer = 0;
        activeTarget = null;
        if (ui && ui.root.isConnected) {
            ui.root.remove();
        }
        ui = null;
    }

    function place() {
        frame = 0;
        if (!ui) {
            return;
        }
        var s = ui.root.style;
        var vw = window.innerWidth;
        var vh = window.innerHeight;
        var cardW = ui.card.offsetWidth;
        var cardH = ui.card.offsetHeight;
        var x;
        var y;

        if (activeTarget && isShown(activeTarget)) {
            var r = activeTarget.getBoundingClientRect();
            s.setProperty("--ptg-tour-spot-x", (r.left - PAD) + "px");
            s.setProperty("--ptg-tour-spot-y", (r.top - PAD) + "px");
            s.setProperty("--ptg-tour-spot-w", (r.width + PAD * 2) + "px");
            s.setProperty("--ptg-tour-spot-h", (r.height + PAD * 2) + "px");

            y = r.bottom + PAD + GAP;
            if (y + cardH > vh - EDGE) {
                y = r.top - PAD - GAP - cardH;
            }
            // در RTL لبهٔ راست کارت با لبهٔ راست عنصر هم‌تراز می‌شود.
            x = document.documentElement.dir === "rtl" ? r.right - cardW : r.left;
        } else {
            s.setProperty("--ptg-tour-spot-x", (vw / 2) + "px");
            s.setProperty("--ptg-tour-spot-y", (vh / 2) + "px");
            s.setProperty("--ptg-tour-spot-w", "0px");
            s.setProperty("--ptg-tour-spot-h", "0px");
            x = (vw - cardW) / 2;
            y = (vh - cardH) / 2;
        }

        x = Math.max(EDGE, Math.min(x, vw - cardW - EDGE));
        y = Math.max(EDGE, Math.min(y, vh - cardH - EDGE));
        s.setProperty("--ptg-tour-card-x", x + "px");
        s.setProperty("--ptg-tour-card-y", y + "px");
    }

    function schedulePlace() {
        if (!frame) {
            frame = window.requestAnimationFrame(place);
        }
    }

    function show(tour, index, target) {
        var steps = TOURS[tour.id];
        var step = steps[index];
        var last = index === steps.length - 1;

        if (!ui) {
            ui = buildUi();
        }
        activeTarget = target;

        ui.meta.textContent = isEnglish()
            ? "Step " + (index + 1) + " of " + steps.length
            : "قدم " + digits(index + 1) + " از " + digits(steps.length);
        ui.title.textContent = tr(step.title);
        ui.text.textContent = !target && step.missing && onPage(step) ? tr(step.missing) : tr(step.text);
        ui.back.textContent = isEnglish() ? "Back" : "قبلی";
        ui.back.hidden = index === 0;
        ui.next.textContent = last ? (isEnglish() ? "Finish" : "تمام") : (isEnglish() ? "Next" : "بعدی");
        ui.skip.setAttribute("aria-label", isEnglish() ? "Close guide" : "بستن راهنما");

        if (target) {
            var r = target.getBoundingClientRect();
            if (r.top < EDGE || r.bottom > window.innerHeight - EDGE) {
                target.scrollIntoView({ block: "center", behavior: reducedMotion() ? "auto" : "smooth" });
            }
        }

        place();
        // فوکوس روی «بعدی» تا Enter تور را جلو ببرد و Esc آن را ببندد.
        ui.next.focus({ preventScroll: true });

        // اسکریپت‌های صفحه (مثل list-toolbar-row.js) گاهی عنصر را بعد از آماده‌شدن جابه‌جا
        // می‌کنند؛ تا تور باز است حفره و کارت دنبال همان عنصر می‌روند.
        if (!followTimer) {
            followTimer = window.setInterval(schedulePlace, 250);
        }
    }

    // ---------- جریان تور ----------
    function state() {
        var st = readJson(window.sessionStorage, STATE_KEY);
        return st && TOURS[st.id] && st.i >= 0 && st.i < TOURS[st.id].length ? st : null;
    }

    function save(st) {
        writeJson(window.sessionStorage, STATE_KEY, st);
    }

    function render() {
        window.clearTimeout(waitTimer);
        var st = state();
        if (!st) {
            removeUi();
            return;
        }

        var steps = TOURS[st.id];
        if (!onPage(steps[st.i])) {
            // کاربر خودش روی عنصر زده و به صفحهٔ قدم بعد رسیده است.
            for (var j = st.i + 1; j < steps.length; j++) {
                if (steps[j].at !== "*" && onPage(steps[j])) {
                    st.i = j;
                    save(st);
                    break;
                }
            }
        }

        var step = steps[st.i];
        if (!onPage(step)) {
            show(st, st.i, null);
            return;
        }

        var started = Date.now();
        (function poll() {
            var target = findTarget(step);
            if (target || Date.now() - started >= WAIT_MS) {
                show(st, st.i, target);
                return;
            }
            waitTimer = window.setTimeout(poll, 100);
        }());
    }

    function start(id, key) {
        if (!TOURS[id]) {
            return;
        }
        save({ id: id, i: 0, key: key || "" });
        var first = TOURS[id][0];
        if (!onPage(first) && first.go) {
            removeUi();
            navigate(first.go);
            return;
        }
        render();
    }

    function go(st, index) {
        var steps = TOURS[st.id];
        var step = steps[index];
        st.i = index;
        save(st);
        if (!onPage(step) && step.go) {
            removeUi();
            navigate(step.go);
            return;
        }
        render();
    }

    function next() {
        var st = state();
        if (!st) {
            return;
        }
        var steps = TOURS[st.id];
        if (st.i >= steps.length - 1) {
            end(true);
            return;
        }

        var step = steps[st.i];
        // کاربر از مسیر بیرون رفته: اول به صفحهٔ همین قدم برگرد.
        if (!onPage(step) && step.go) {
            removeUi();
            navigate(step.go);
            return;
        }
        var nextStep = steps[st.i + 1];
        // قدمی که کاربر را جلو می‌برد (منو، دکمه، تب): همان کلیک واقعی را انجام بده.
        if (step.click && activeTarget && onPage(step)) {
            var target = activeTarget;
            var samePage = onPage(nextStep);
            st.i += 1;
            save(st);
            removeUi();
            target.click();
            // اگر کلیک صفحه را عوض کند، ptg:page-ready تور را ادامه می‌دهد؛ وگرنه همین‌جا.
            if (samePage) {
                window.setTimeout(render, 400);
            }
            return;
        }
        // قدم انتخاب: تیک ردیف را خودش می‌زند تا دکمه‌های همان ردیف ظاهر شوند.
        if (step.tick && activeTarget && activeTarget.type === "checkbox" && !activeTarget.checked) {
            activeTarget.click();
        }
        go(st, st.i + 1);
    }

    function back() {
        var st = state();
        if (!st || st.i === 0) {
            return;
        }
        go(st, st.i - 1);
    }

    function end(finished) {
        var st = state();
        if (finished && st && st.key) {
            var seen = readJson(window.localStorage, st.key + ":tours") || [];
            if (seen.indexOf(st.id) < 0) {
                seen.push(st.id);
                writeJson(window.localStorage, st.key + ":tours", seen);
            }
        }
        save(null);
        removeUi();
        markSeen();
    }

    // ---------- کارت داشبورد ----------
    function panelKey() {
        var panel = document.querySelector("[data-onboarding-panel]");
        return panel ? panel.getAttribute("data-onboarding-key") || "" : "";
    }

    function markSeen() {
        var key = panelKey();
        if (!key) {
            return;
        }
        var seen = readJson(window.localStorage, key + ":tours") || [];
        document.querySelectorAll("[data-onboarding-panel] .ptg-onb-task[data-tour-start]").forEach(function (btn) {
            btn.classList.toggle("is-seen", seen.indexOf(btn.getAttribute("data-tour-start")) >= 0);
        });
    }

    function initPanel() {
        var panel = document.querySelector("[data-onboarding-panel]");
        if (!panel) {
            return;
        }
        if (panel.getAttribute("data-onboarding-force") === "1") {
            // کاربر از دکمهٔ «راهنمای شروع» آمده: بستن قبلی لغو می‌شود.
            writeCookie(panel.getAttribute("data-onboarding-key"), "");
        }
        markSeen();
    }

    function dismiss(button) {
        var panel = button.closest("[data-onboarding-panel]");
        if (!panel) {
            return;
        }
        var key = panel.getAttribute("data-onboarding-key");
        var kind = panel.getAttribute("data-onboarding-panel");
        var flags = readCookie(key).split(".").filter(Boolean);
        if (flags.indexOf(kind) < 0) {
            flags.push(kind);
        }
        writeCookie(key, flags.join("."));
        panel.remove();
        if (typeof window.ptgToast === "function") {
            window.ptgToast("info", isEnglish()
                ? "You can reopen the guide from the signpost button at the top."
                : "راهنما را هر وقت خواستید از دکمهٔ تابلو در بالای صفحه باز کنید.");
        }
    }

    document.addEventListener("click", function (e) {
        var starter = e.target.closest("[data-tour-start]");
        if (starter && starter.getAttribute("data-tour-start")) {
            e.preventDefault();
            e.stopPropagation();
            start(starter.getAttribute("data-tour-start"), panelKey());
            return;
        }
        var closer = e.target.closest("[data-onboarding-dismiss]");
        if (closer) {
            dismiss(closer);
        }
    }, true);

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && ui) {
            end(false);
        }
    });

    window.addEventListener("resize", schedulePlace);
    window.addEventListener("scroll", schedulePlace, true);

    function onReady() {
        initPanel();
        render();
    }

    // SPA فقط <main> را عوض می‌کند؛ هر بار صفحه آماده شد، کارت و تور دوباره بررسی می‌شوند.
    window.addEventListener("ptg:page-ready", function () {
        removeUi();
        onReady();
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", onReady, { once: true });
    } else {
        onReady();
    }
}());
