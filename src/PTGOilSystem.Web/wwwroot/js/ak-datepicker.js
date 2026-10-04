(function () {
    "use strict";

    // Shared date picker: progressive enhancement over native <input type="date">.
    // The native input stays in the DOM as the single value / binding / validation
    // source (ISO yyyy-mm-dd). This layer only supplies a calibrated display field
    // ("13 Jul 2026") and a light calendar popup. No markup or value semantics change.

    var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    var WEEK = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];
    var CAL_SVG = '<svg viewBox="0 0 20 20" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4.5" width="14" height="12.5" rx="2"/><path d="M3 8h14M7 3v3M13 3v3"/></svg>';
    var CHEV = '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 3 5 8l5 5"/></svg>';

    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function iso(y, m, d) { return y + "-" + pad(m + 1) + "-" + pad(d); }

    // -----------------------------------------------------------------
    // Afghan Solar Hijri (هجری شمسی افغانستان) — display/input layer only.
    // The native input keeps the canonical Gregorian ISO value; the server
    // (PersianCalendar + UtcDateTimeModelBinder) stays the source of truth.
    // Conversion follows the jalaali algorithm (Borkowski breaks table),
    // which matches .NET PersianCalendar day-for-day over the supported 1200–1500 range.
    // -----------------------------------------------------------------
    var SOLAR_MONTHS = ["حمل", "ثور", "جوزا", "سرطان", "اسد", "سنبله", "میزان", "عقرب", "قوس", "جدی", "دلو", "حوت"];
    var SOLAR_WEEK = ["ش", "ی", "د", "س", "چ", "پ", "ج"];   // week starts on Saturday
    var SOLAR_MIN_YEAR = 1200;
    var SOLAR_MAX_YEAR = 1500;
    var SOLAR_EXAMPLE = "1405/07/12";
    var SOLAR_MSG = {
        format: "تاریخ را به شکل سال/ماه/روز بنویسید؛ مثال: " + SOLAR_EXAMPLE,
        year: "سال هجری شمسی معتبر نیست؛ سال باید بین " + SOLAR_MIN_YEAR + " و " + SOLAR_MAX_YEAR + " باشد.",
        month: "ماه باید عددی از ۱ تا ۱۲ باشد.",
        range: "این تاریخ خارج از بازهٔ مجاز است."
    };
    var BREAKS = [-61, 9, 38, 199, 426, 686, 756, 818, 1111, 1181, 1210, 1635, 2060, 2097, 2192, 2262, 2324, 2394, 2456, 3178];

    function div(a, b) { return ~~(a / b); }
    function mod(a, b) { return a - ~~(a / b) * b; }

    function jalCal(jy) {
        var bl = BREAKS.length, gy = jy + 621, leapJ = -14, jp = BREAKS[0], jm, jump = 0, leap, leapG, march, n, i;
        for (i = 1; i < bl; i += 1) {
            jm = BREAKS[i];
            jump = jm - jp;
            if (jy < jm) break;
            leapJ = leapJ + div(jump, 33) * 8 + div(mod(jump, 33), 4);
            jp = jm;
        }
        n = jy - jp;
        leapJ = leapJ + div(n, 33) * 8 + div(mod(n, 33) + 3, 4);
        if (mod(jump, 33) === 4 && jump - n === 4) leapJ += 1;
        leapG = div(gy, 4) - div((div(gy, 100) + 1) * 3, 4) - 150;
        march = 20 + leapJ - leapG;
        if (jump - n < 6) n = n - jump + div(jump + 4, 33) * 33;
        leap = mod(mod(n + 1, 33) - 1, 4);
        if (leap === -1) leap = 4;
        return { leap: leap, gy: gy, march: march };
    }

    function g2d(gy, gm, gd) {
        var d = div((gy + div(gm - 8, 6) + 100100) * 1461, 4) + div(153 * mod(gm + 9, 12) + 2, 5) + gd - 34840408;
        return d - div(div(gy + 100100 + div(gm - 8, 6), 100) * 3, 4) + 752;
    }

    function d2g(jdn) {
        var j = 4 * jdn + 139361631;
        j = j + div(div(4 * jdn + 183187720, 146097) * 3, 4) * 4 - 3908;
        var i = div(mod(j, 1461), 4) * 5 + 308;
        var gd = div(mod(i, 153), 5) + 1;
        var gm = mod(div(i, 153), 12) + 1;
        var gy = div(j, 1461) - 100100 + div(8 - gm, 6);
        return { y: gy, m: gm, d: gd };
    }

    function j2d(jy, jm, jd) {
        var r = jalCal(jy);
        return g2d(r.gy, 3, r.march) + (jm - 1) * 31 - div(jm, 7) * (jm - 7) + jd - 1;
    }

    function d2j(jdn) {
        var gy = d2g(jdn).y, jy = gy - 621, r = jalCal(jy), k = jdn - g2d(gy, 3, r.march);
        if (k >= 0) {
            if (k <= 185) return { y: jy, m: 1 + div(k, 31), d: mod(k, 31) + 1 };
            k -= 186;
        } else {
            jy -= 1;
            k += 179;
            if (r.leap === 1) k += 1;
        }
        return { y: jy, m: 7 + div(k, 30), d: mod(k, 30) + 1 };
    }

    function solarLeap(jy) { return jalCal(jy).leap === 0; }
    function solarMonthLength(jy, jm) { return jm <= 6 ? 31 : jm <= 11 ? 30 : (solarLeap(jy) ? 30 : 29); }

    function solarFromIso(value) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec((value || "").trim());
        return m ? d2j(g2d(+m[1], +m[2], +m[3])) : null;
    }

    function solarToIso(jy, jm, jd) {
        var g = d2g(j2d(jy, jm, jd));
        return g.y + "-" + pad(g.m) + "-" + pad(g.d);
    }

    function solarText(s) { return s.y + "/" + pad(s.m) + "/" + pad(s.d); }

    function normalizeDigits(text) {
        return String(text || "")
            .replace(/[۰-۹]/g, function (c) { return String(c.charCodeAt(0) - 0x06F0); })
            .replace(/[٠-٩]/g, function (c) { return String(c.charCodeAt(0) - 0x0660); })
            .replace(/[‎‏‌؜]/g, "")
            .replace(/[٫∕⁄]/g, "/")
            .trim();
    }

    // Same rules and Dari messages as AfghanSolarCalendar.TryParse on the server.
    function parseSolar(text) {
        var m = /^(\d{1,4})\s*[\/\-.]\s*(\d{1,2})\s*[\/\-.]\s*(\d{1,2})$/.exec(normalizeDigits(text));
        if (!m) return { error: SOLAR_MSG.format };
        var y = +m[1], mo = +m[2], d = +m[3];
        if (y < SOLAR_MIN_YEAR || y > SOLAR_MAX_YEAR) return { error: SOLAR_MSG.year };
        if (mo < 1 || mo > 12) return { error: SOLAR_MSG.month };
        var len = solarMonthLength(y, mo);
        if (d < 1 || d > len) return { error: "ماه " + SOLAR_MONTHS[mo - 1] + " سال " + y + " فقط " + len + " روز دارد." };
        return { y: y, m: mo, d: d };
    }

    function isSolarMode() {
        return !!document.body && document.body.getAttribute("data-calendar") === "solar";
    }

    // Shared helpers for other scripts (filter chips, custom pickers).
    window.PTG = window.PTG || {};
    window.PTG.calendar = {
        isSolar: isSolarMode,
        monthNames: SOLAR_MONTHS.slice(),
        solarFromIso: solarFromIso,
        solarToIso: solarToIso,
        solarMonthLength: solarMonthLength,
        parseSolar: parseSolar,
        normalizeDigits: normalizeDigits,
        // ISO "yyyy-mm-dd" → "1405/07/12" in Solar mode; unchanged otherwise.
        formatIso: function (value) {
            if (!isSolarMode()) return value;
            var s = solarFromIso(value);
            return s ? solarText(s) : value;
        },
        // ISO → "12 میزان 1405" in Solar mode; null otherwise.
        formatIsoLong: function (value) {
            var s = isSolarMode() ? solarFromIso(value) : null;
            return s ? s.d + " " + SOLAR_MONTHS[s.m - 1] + " " + s.y : null;
        }
    };

    // The calendar is portalled to a body-level overlay: an ancestor with
    // `overflow:auto` (e.g. the search/filter popover) would otherwise clip it,
    // and no z-index can escape that clip.
    var GAP = 6;
    var overlayRoot = null;
    function overlay() {
        if (overlayRoot && document.body.contains(overlayRoot)) return overlayRoot;
        overlayRoot = document.querySelector(".ak-overlay-root");
        if (!overlayRoot) {
            overlayRoot = document.createElement("div");
            overlayRoot.className = "ak-overlay-root";
            document.body.appendChild(overlayRoot);
        }
        return overlayRoot;
    }

    function isRtl() {
        return (document.documentElement.getAttribute("dir") || "").toLowerCase() === "rtl";
    }

    function place(pop, anchor) {
        var r = anchor.getBoundingClientRect();
        var w = pop.offsetWidth || 268;
        var h = pop.offsetHeight || 300;
        var vw = document.documentElement.clientWidth;
        var vh = document.documentElement.clientHeight;

        var left = isRtl() ? r.right - w : r.left;
        left = Math.max(8, Math.min(left, vw - w - 8));

        var top = r.bottom + GAP;
        if (top + h > vh - 8 && r.top - GAP - h > 8) top = r.top - GAP - h;  // flip above
        top = Math.max(8, Math.min(top, vh - h - 8));

        pop.style.left = Math.round(left) + "px";
        pop.style.top = Math.round(top) + "px";
    }

    function parseISO(value) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec((value || "").trim());
        if (!m) return null;
        var d = new Date(+m[1], +m[2] - 1, +m[3]);
        return isNaN(d.getTime()) ? null : d;
    }

    function format(date) {
        return date.getDate() + " " + MONTHS[date.getMonth()] + " " + date.getFullYear();
    }

    function enhance(native) {
        if (!native || native.dataset.akDateReady === "true") return;
        if (native.dataset.akDatepicker === "false" || native.closest("[data-loading-date-picker]")) return;
        native.dataset.akDateReady = "true";
        if (isSolarMode()) { enhanceSolar(native); return; }

        var minDate = parseISO(native.getAttribute("min"));
        var maxDate = parseISO(native.getAttribute("max"));

        var root = document.createElement("div");
        root.className = "ak-datepicker";
        root.dataset.open = "false";

        var field = document.createElement("button");
        field.type = "button";
        field.className = "ak-date-field";
        field.setAttribute("aria-haspopup", "dialog");
        field.setAttribute("aria-expanded", "false");

        var display = document.createElement("span");
        display.className = "ak-date-display";

        var icon = document.createElement("span");
        icon.className = "ak-date-icon";
        icon.setAttribute("aria-hidden", "true");
        icon.innerHTML = CAL_SVG;

        field.appendChild(display);
        field.appendChild(icon);

        var pop = document.createElement("div");
        pop.className = "ak-date-pop";
        pop.setAttribute("role", "dialog");
        pop.setAttribute("dir", "ltr");
        pop.innerHTML =
            '<div class="ak-date-head">' +
                '<button type="button" class="ak-date-nav" data-nav="-1" aria-label="Previous month">' + CHEV + '</button>' +
                '<button type="button" class="ak-date-title" aria-label="Choose year"></button>' +
                '<button type="button" class="ak-date-nav ak-date-nav-next" data-nav="1" aria-label="Next month">' + CHEV + '</button>' +
            '</div>' +
            '<div class="ak-date-week"></div>' +
            '<div class="ak-date-grid"></div>' +
            '<div class="ak-date-years" hidden></div>';

        native.parentNode.insertBefore(root, native);
        root.appendChild(native);
        native.classList.add("ak-date-native");
        root.appendChild(field);
        root.appendChild(pop);

        var title = pop.querySelector(".ak-date-title");
        var weekRow = pop.querySelector(".ak-date-week");
        var grid = pop.querySelector(".ak-date-grid");
        var years = pop.querySelector(".ak-date-years");
        weekRow.innerHTML = WEEK.map(function (w) { return '<span>' + w + '</span>'; }).join("");

        var view = new Date();
        view.setDate(1);

        function selected() { return parseISO(native.value); }

        function disabledDay(y, m, d) {
            var t = new Date(y, m, d).getTime();
            if (minDate && t < minDate.getTime()) return true;
            if (maxDate && t > maxDate.getTime()) return true;
            return false;
        }

        function syncDisplay() {
            var sel = selected();
            display.textContent = sel ? format(sel) : (native.dataset.akPlaceholder || "");
            root.dataset.empty = sel ? "false" : "true";
            var off = native.disabled || native.readOnly;
            field.disabled = native.disabled;
            root.dataset.readonly = native.readOnly ? "true" : "false";
            root.dataset.invalid = native.classList.contains("input-validation-error") ? "true" : "false";
        }

        function renderDays() {
            years.hidden = true;
            grid.hidden = false;
            weekRow.style.display = "";
            var y = view.getFullYear(), m = view.getMonth();
            title.textContent = MONTHS[m] + " " + y;
            var first = new Date(y, m, 1).getDay();
            var days = new Date(y, m + 1, 0).getDate();
            var prevDays = new Date(y, m, 0).getDate();
            var sel = selected();
            var today = new Date();
            var cells = "";
            for (var i = 0; i < first; i++) {
                cells += '<button type="button" class="ak-date-day is-outside" tabindex="-1" disabled>' + (prevDays - first + 1 + i) + '</button>';
            }
            for (var d = 1; d <= days; d++) {
                var cls = "ak-date-day";
                if (disabledDay(y, m, d)) cls += " is-disabled";
                if (sel && sel.getFullYear() === y && sel.getMonth() === m && sel.getDate() === d) cls += " is-selected";
                if (today.getFullYear() === y && today.getMonth() === m && today.getDate() === d) cls += " is-today";
                cells += '<button type="button" class="' + cls + '" data-day="' + d + '"' + (disabledDay(y, m, d) ? " disabled" : "") + '>' + d + '</button>';
            }
            grid.innerHTML = cells;
        }

        function renderYears() {
            grid.hidden = true;
            weekRow.style.display = "none";
            years.hidden = false;
            var cur = view.getFullYear();
            var start = cur - 6;
            var html = "";
            for (var i = 0; i < 12; i++) {
                var yr = start + i;
                html += '<button type="button" class="ak-date-year' + (yr === cur ? " is-selected" : "") + '" data-year="' + yr + '">' + yr + '</button>';
            }
            years.innerHTML = html;
            title.textContent = start + " – " + (start + 11);
        }

        function reposition() { if (root.dataset.open === "true") place(pop, field); }

        function open() {
            if (native.disabled || native.readOnly) return;
            var sel = selected();
            if (sel) { view = new Date(sel.getFullYear(), sel.getMonth(), 1); }
            root.dataset.open = "true";
            field.setAttribute("aria-expanded", "true");
            renderDays();
            overlay().appendChild(pop);   // portal out of any clipping ancestor
            pop.classList.add("is-open");
            place(pop, field);
            window.addEventListener("scroll", reposition, true);
            window.addEventListener("resize", reposition);
        }

        function close() {
            root.dataset.open = "false";
            field.setAttribute("aria-expanded", "false");
            years.hidden = true;
            pop.classList.remove("is-open");
            window.removeEventListener("scroll", reposition, true);
            window.removeEventListener("resize", reposition);
            if (pop.parentNode !== root) root.appendChild(pop);   // back home when closed
        }

        function pick(y, m, d) {
            native.value = iso(y, m, d);
            native.dispatchEvent(new Event("input", { bubbles: true }));
            native.dispatchEvent(new Event("change", { bubbles: true }));
            syncDisplay();
            close();
            field.focus();
        }

        field.addEventListener("click", function () {
            if (root.dataset.open === "true") { close(); } else { open(); }
        });

        pop.addEventListener("click", function (event) {
            var nav = event.target.closest("[data-nav]");
            if (nav) { view.setMonth(view.getMonth() + (+nav.dataset.nav)); renderDays(); return; }
            if (event.target.closest(".ak-date-title")) {
                if (years.hidden) { renderYears(); } else { renderDays(); }
                return;
            }
            var yr = event.target.closest("[data-year]");
            if (yr) { view.setFullYear(+yr.dataset.year); renderDays(); return; }
            var day = event.target.closest("[data-day]:not([disabled])");
            if (day) { pick(view.getFullYear(), view.getMonth(), +day.dataset.day); }
        });

        field.addEventListener("keydown", function (event) {
            if ((event.key === "ArrowDown" || event.key === "Enter" || event.key === " ") && root.dataset.open !== "true") {
                event.preventDefault();
                open();
            } else if (event.key === "Escape" && root.dataset.open === "true") {
                event.preventDefault();
                close();
            }
        });

        root.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && root.dataset.open === "true") {
                event.preventDefault();
                close();
                field.focus();
            }
        });

        // Keep the label[for] behaviour: focusing the native input focuses the field.
        native.addEventListener("focus", function () { field.focus(); });
        native.addEventListener("change", syncDisplay);

        root._akDateClose = close;
        root._akDatePop = pop;
        pop._akDateOwner = root;

        new MutationObserver(syncDisplay).observe(native, {
            attributes: true,
            attributeFilter: ["value", "disabled", "readonly", "class", "min", "max"]
        });

        syncDisplay();
    }

    function labelFor(native) {
        if (native.getAttribute("aria-label")) return native.getAttribute("aria-label");
        var label = native.id ? document.querySelector('label[for="' + native.id + '"]') : null;
        if (!label) label = native.closest("label");
        return label ? label.textContent.trim() : "";
    }

    // Solar Hijri field: a typeable text box (Latin or Persian/Arabic digits) plus a
    // Solar calendar popup. Only the hidden native input is submitted, always as
    // Gregorian ISO, so model binding and every existing script keep working.
    function enhanceSolar(native) {
        var minIso = (native.getAttribute("min") || "").trim();
        var maxIso = (native.getAttribute("max") || "").trim();

        var root = document.createElement("div");
        root.className = "ak-datepicker is-solar";
        root.dataset.open = "false";

        var field = document.createElement("div");
        field.className = "ak-date-field ak-date-field-solar";

        var text = document.createElement("input");
        text.type = "text";
        text.className = "ak-date-text";
        text.setAttribute("dir", "ltr");
        text.setAttribute("inputmode", "numeric");
        text.setAttribute("autocomplete", "off");
        text.setAttribute("placeholder", SOLAR_EXAMPLE);
        var label = labelFor(native);
        if (label) text.setAttribute("aria-label", label);

        var iconBtn = document.createElement("button");
        iconBtn.type = "button";
        iconBtn.className = "ak-date-icon ak-date-icon-btn";
        iconBtn.setAttribute("aria-haspopup", "dialog");
        iconBtn.setAttribute("aria-expanded", "false");
        iconBtn.setAttribute("aria-label", "انتخاب تاریخ از تقویم");
        iconBtn.innerHTML = CAL_SVG;

        field.appendChild(text);
        field.appendChild(iconBtn);

        var error = document.createElement("span");
        error.className = "field-validation-error ak-date-error";
        error.setAttribute("role", "alert");
        error.hidden = true;

        var pop = document.createElement("div");
        pop.className = "ak-date-pop";
        pop.setAttribute("role", "dialog");
        pop.setAttribute("dir", "rtl");
        pop.innerHTML =
            '<div class="ak-date-head">' +
                '<button type="button" class="ak-date-nav ak-date-nav-next" data-nav="-1" aria-label="ماه قبل">' + CHEV + '</button>' +
                '<button type="button" class="ak-date-title" aria-label="انتخاب سال"></button>' +
                '<button type="button" class="ak-date-nav" data-nav="1" aria-label="ماه بعد">' + CHEV + '</button>' +
            '</div>' +
            '<div class="ak-date-week"></div>' +
            '<div class="ak-date-grid"></div>' +
            '<div class="ak-date-years" hidden></div>';

        native.parentNode.insertBefore(root, native);
        root.appendChild(native);
        native.classList.add("ak-date-native");
        root.appendChild(field);
        root.appendChild(error);
        root.appendChild(pop);

        var title = pop.querySelector(".ak-date-title");
        var weekRow = pop.querySelector(".ak-date-week");
        var grid = pop.querySelector(".ak-date-grid");
        var years = pop.querySelector(".ak-date-years");
        weekRow.innerHTML = SOLAR_WEEK.map(function (w) { return "<span>" + w + "</span>"; }).join("");

        var now = new Date();
        var todayIso = now.getFullYear() + "-" + pad(now.getMonth() + 1) + "-" + pad(now.getDate());
        var view = solarFromIso(todayIso);
        var selfWrite = false;   // true while this field itself writes the native value

        function syncState() {
            text.disabled = native.disabled;
            text.readOnly = native.readOnly;
            iconBtn.disabled = native.disabled || native.readOnly;
            root.dataset.readonly = native.readOnly ? "true" : "false";
            root.dataset.empty = native.value ? "false" : "true";
            root.dataset.invalid = (root.dataset.solarInvalid === "true" || native.classList.contains("input-validation-error")) ? "true" : "false";
        }

        function setError(message) {
            error.textContent = message || "";
            error.hidden = !message;
            root.dataset.solarInvalid = message ? "true" : "false";
            text.setAttribute("aria-invalid", message ? "true" : "false");
            syncState();
        }

        // Native (canonical) value → visible Solar text.
        function syncFromNative() {
            var s = solarFromIso(native.value);
            text.value = s ? solarText(s) : "";
            setError(null);
        }

        function setNative(value) {
            if (native.value === value) { syncState(); return; }
            selfWrite = true;
            try {
                native.value = value;
                native.dispatchEvent(new Event("input", { bubbles: true }));
                native.dispatchEvent(new Event("change", { bubbles: true }));
            } finally {
                selfWrite = false;
            }
            syncState();
        }

        // Visible Solar text → native ISO. Returns false (and shows the Dari message) when invalid.
        function commit() {
            var raw = normalizeDigits(text.value);
            if (!raw) {
                setNative("");
                setError(null);
                return true;
            }
            var parsed = parseSolar(raw);
            if (parsed.error) {
                setError(parsed.error);
                return false;
            }
            var value = solarToIso(parsed.y, parsed.m, parsed.d);
            if ((minIso && value < minIso) || (maxIso && value > maxIso)) {
                setError(SOLAR_MSG.range);
                return false;
            }
            text.value = solarText(parsed);
            setError(null);
            setNative(value);
            return true;
        }

        function disabledIso(value) {
            return (minIso && value < minIso) || (maxIso && value > maxIso);
        }

        function renderDays() {
            years.hidden = true;
            grid.hidden = false;
            weekRow.style.display = "";
            title.textContent = SOLAR_MONTHS[view.m - 1] + " " + view.y;
            var first = /^(\d{4})-(\d{2})-(\d{2})$/.exec(solarToIso(view.y, view.m, 1));
            var lead = (new Date(+first[1], +first[2] - 1, +first[3]).getDay() + 1) % 7;   // Saturday = 0
            var days = solarMonthLength(view.y, view.m);
            var prevDays = view.m === 1 ? solarMonthLength(view.y - 1, 12) : solarMonthLength(view.y, view.m - 1);
            var sel = solarFromIso(native.value);
            var today = solarFromIso(todayIso);
            var cells = "";
            for (var i = 0; i < lead; i++) {
                cells += '<button type="button" class="ak-date-day is-outside" tabindex="-1" disabled>' + (prevDays - lead + 1 + i) + "</button>";
            }
            for (var d = 1; d <= days; d++) {
                var off = disabledIso(solarToIso(view.y, view.m, d));
                var cls = "ak-date-day";
                if (off) cls += " is-disabled";
                if (sel && sel.y === view.y && sel.m === view.m && sel.d === d) cls += " is-selected";
                if (today && today.y === view.y && today.m === view.m && today.d === d) cls += " is-today";
                cells += '<button type="button" class="' + cls + '" data-day="' + d + '"' + (off ? " disabled" : "") + ">" + d + "</button>";
            }
            grid.innerHTML = cells;
        }

        function renderYears() {
            grid.hidden = true;
            weekRow.style.display = "none";
            years.hidden = false;
            var start = Math.max(SOLAR_MIN_YEAR, view.y - 6);
            var html = "";
            for (var i = 0; i < 12 && start + i <= SOLAR_MAX_YEAR; i++) {
                var yr = start + i;
                html += '<button type="button" class="ak-date-year' + (yr === view.y ? " is-selected" : "") + '" data-year="' + yr + '">' + yr + "</button>";
            }
            years.innerHTML = html;
            title.textContent = start + " – " + (start + 11);
        }

        function reposition() { if (root.dataset.open === "true") place(pop, field); }

        function open() {
            if (native.disabled || native.readOnly) return;
            var sel = solarFromIso(native.value);
            if (sel) view = { y: sel.y, m: sel.m, d: 1 };
            root.dataset.open = "true";
            iconBtn.setAttribute("aria-expanded", "true");
            renderDays();
            overlay().appendChild(pop);
            pop.classList.add("is-open");
            place(pop, field);
            window.addEventListener("scroll", reposition, true);
            window.addEventListener("resize", reposition);
        }

        function close() {
            root.dataset.open = "false";
            iconBtn.setAttribute("aria-expanded", "false");
            years.hidden = true;
            pop.classList.remove("is-open");
            window.removeEventListener("scroll", reposition, true);
            window.removeEventListener("resize", reposition);
            if (pop.parentNode !== root) root.appendChild(pop);
        }

        function shiftMonth(step) {
            var m = view.m + step, y = view.y;
            if (m < 1) { m = 12; y -= 1; }
            if (m > 12) { m = 1; y += 1; }
            if (y < SOLAR_MIN_YEAR || y > SOLAR_MAX_YEAR) return;
            view = { y: y, m: m, d: 1 };
            renderDays();
        }

        iconBtn.addEventListener("click", function () {
            if (root.dataset.open === "true") { close(); } else { open(); }
        });

        pop.addEventListener("click", function (event) {
            var nav = event.target.closest("[data-nav]");
            if (nav) { shiftMonth(+nav.dataset.nav); return; }
            if (event.target.closest(".ak-date-title")) {
                if (years.hidden) { renderYears(); } else { renderDays(); }
                return;
            }
            var yr = event.target.closest("[data-year]");
            if (yr) { view = { y: +yr.dataset.year, m: view.m, d: 1 }; renderDays(); return; }
            var day = event.target.closest("[data-day]:not([disabled])");
            if (day) {
                text.value = solarText({ y: view.y, m: view.m, d: +day.dataset.day });
                commit();
                close();
                text.focus();
            }
        });

        text.addEventListener("input", function () { if (root.dataset.solarInvalid === "true") setError(null); });
        text.addEventListener("change", commit);
        text.addEventListener("blur", commit);
        text.addEventListener("keydown", function (event) {
            if (event.key === "Enter") {
                // The submit guard below blocks the form if the typed value is invalid.
                commit();
            } else if (event.key === "ArrowDown" && event.altKey) {
                event.preventDefault();
                open();
            }
        });

        root.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && root.dataset.open === "true") {
                event.preventDefault();
                close();
                text.focus();
            }
        });

        // Keep the label[for] behaviour, and follow values set by other scripts.
        native.addEventListener("focus", function () { text.focus(); });
        native.addEventListener("change", function () {
            if (!selfWrite) syncFromNative();
            syncState();
        });

        root._akDateClose = close;
        root._akDatePop = pop;
        root._akDateCommit = commit;
        root._akDateText = text;
        pop._akDateOwner = root;

        new MutationObserver(syncFromNative).observe(native, { attributes: true, attributeFilter: ["value"] });

        // Other scripts (row imports, defaults, copy-from-source) assign native.value
        // directly without events; mirror those writes into the Solar text.
        var valueProperty = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
        if (valueProperty && valueProperty.set && valueProperty.get) {
            Object.defineProperty(native, "value", {
                configurable: true,
                get: function () { return valueProperty.get.call(native); },
                set: function (next) {
                    valueProperty.set.call(native, next);
                    if (!selfWrite) syncFromNative();
                    syncState();
                }
            });
        }
        new MutationObserver(syncState).observe(native, { attributes: true, attributeFilter: ["disabled", "readonly", "class"] });

        syncFromNative();
        // The server-rendered Solar value (CalendarDateInputTagHelper) wins for the initial display.
        if (native.dataset.solarValue && native.value === native.getAttribute("value")) {
            text.value = native.dataset.solarValue;
        }
    }

    // Solar mode: an invalid typed date must never post a stale or empty value.
    // Capture phase runs before jQuery validation and SPA form handling.
    function guardSolarSubmit(event) {
        var form = event.target;
        if (!form || !form.querySelectorAll) return;
        var invalid = null;
        form.querySelectorAll(".ak-datepicker.is-solar").forEach(function (root) {
            if (typeof root._akDateCommit === "function" && !root._akDateCommit() && !invalid) invalid = root;
        });
        if (!invalid) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        if (invalid._akDateText) invalid._akDateText.focus();
    }

    function scan(node) {
        if (!node) return;
        if (node.matches && node.matches('input[type="date"]')) enhance(node);
        if (node.querySelectorAll) node.querySelectorAll('input[type="date"]').forEach(enhance);
    }

    function start() {
        scan(document);
        document.addEventListener("submit", guardSolarSubmit, true);
        document.addEventListener("pointerdown", function (event) {
            document.querySelectorAll('.ak-datepicker[data-open="true"]').forEach(function (root) {
                // While open the calendar lives in the overlay, not inside root.
                var pop = root._akDatePop;
                var inside = root.contains(event.target) || (pop && pop.contains(event.target));
                if (!inside && typeof root._akDateClose === "function") root._akDateClose();
            });
        });
        // SPA navigation can drop the owner without closing: never leave a portalled
        // calendar orphaned in the overlay.
        window.addEventListener("ptg:page-ready", function () {
            var host = document.querySelector(".ak-overlay-root");
            if (!host) return;
            Array.prototype.forEach.call(host.children, function (pop) {
                var owner = pop._akDateOwner;
                if (!owner || !document.body.contains(owner)) pop.remove();
                else if (typeof owner._akDateClose === "function") owner._akDateClose();
            });
        });
        new MutationObserver(function (records) {
            records.forEach(function (record) { record.addedNodes.forEach(scan); });
        }).observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start, { once: true });
    } else {
        start();
    }
})();
