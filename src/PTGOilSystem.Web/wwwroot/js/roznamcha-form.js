(function () {
    window.PTG = window.PTG || {};
    if (window.PTG.roznamchaTimeInterval) {
        window.clearInterval(window.PTG.roznamchaTimeInterval);
        window.PTG.roznamchaTimeInterval = null;
    }

    const directionSelect = document.getElementById("Direction");
    const counterpartySelect = document.getElementById("CounterpartyType");
    const paymentKindSelect = document.getElementById("PaymentKind");
    const journalForm = document.getElementById("roznamchaForm");
    const cashAccountSelect = document.getElementById("cashAccountSelect");
    const currencySelect = document.getElementById("paymentCurrency");
    const usdNote = document.getElementById("paymentUsdNote");
    const fxDocGroup = document.getElementById("paymentFxDocGroup");
    const fxInput = document.getElementById("paymentFxRateInput");
    const fxCurrencyNameTargets = Array.from(document.querySelectorAll("[data-fx-currency-name]"));

    // Payment method controls (نقد/بانک یا از طریق صراف)
    const paymentMethodInput = document.getElementById("paymentMethodInput");
    const paymentMethodChoices = Array.from(document.querySelectorAll("[data-payment-method-choice]"));
    // فیلدهای مسیر نقد/بانک در دو fieldset پخش شده‌اند (فیلدهای اصلی و «جزئیات بیشتر»)؛
    // هر دو با هم نمایش/مخفی و enable/disable می‌شوند.
    const cashbankSections = Array.from(document.querySelectorAll("[data-payment-cashbank-section]"));
    const cashbankOnlyControls = Array.from(document.querySelectorAll("[data-cashbank-only]"));
    const sarrafSection = document.querySelector("[data-payment-sarraf-section]");

    // فیلدها و پنل خلاصهٔ «پرداخت از طریق صراف» (محاسبهٔ زنده، فقط نمایشی).
    const sarrafSupplierAmountInput = document.getElementById("SarrafSupplierAmount");
    const sarrafSupplierRateInput = document.getElementById("SarrafSupplierPerUsdRate");
    const sarrafCompanyRateInput = document.getElementById("SarrafCompanyPerUsdRate");
    const sarrafCompanyRateField = document.querySelector("[data-sarraf-company-rate-field]");
    const sarrafFxDiffField = document.querySelector("[data-sarraf-fxdiff-field]");
    const sarrafSingleRateNote = document.querySelector("[data-sarraf-single-rate-note]");
    const sarrafDualRateNote = document.querySelector("[data-sarraf-dual-rate-note]");
    const sarrafCurrencySelect = document.getElementById("sarrafCurrency");
    const sarrafSummaryPanel = document.querySelector("[data-sarraf-summary]");
    const sarrafSummaryHold = document.querySelector("[data-sarraf-summary-hold]");
    const sarrafSummaryContract = document.querySelector("[data-sarraf-summary-contract]");
    const sarrafRateField = document.querySelector("[data-sarraf-rate-field]");

    // جهت و نوع طرف‌حساب صراف (Phase 1 عمومی‌سازی).
    // SarrafDirection: 2=Out (صراف پرداخت کرد)، 1=In (صراف دریافت کرد).
    // SarrafCounterpartyType: 1=تأمین‌کننده، 2=مشتری، 3=شرکت خدماتی.
    const sarrafDirectionInput = document.getElementById("sarrafDirectionInput");
    const sarrafDirectionChoices = Array.from(document.querySelectorAll("[data-sarraf-direction-choice]"));
    const sarrafCounterpartyTypeSelect = document.getElementById("sarrafCounterpartyType");
    const sarrafPartyPanels = Array.from(document.querySelectorAll("[data-sarraf-party]"));
    const sarrafHintOut = document.querySelector("[data-sarraf-hint-out]");
    const sarrafHintIn = document.querySelector("[data-sarraf-hint-in]");
    const sarrafAmountLabel = document.querySelector("[data-sarraf-amount-label]");
    const sarrafAllocSection = document.querySelector("[data-sarraf-alloc-section]");
    const SARRAF_DIR_OUT = "2";
    const SARRAF_DIR_IN = "1";

    // تخصیص به قرارداد (فقط UI): «نزد تأمین‌کننده بماند» یا «همین حالا به قرارداد وصل شود».
    const sarrafAllocChoices = Array.from(document.querySelectorAll("[data-sarraf-alloc-choice]"));
    const sarrafContractFields = document.querySelector("[data-sarraf-contract-fields]");
    const sarrafContractSelect = document.getElementById("sarrafContractSelect");
    let sarrafAllocMode = "hold";

    // نام دری ارزها برای برچسب «نرخ دالر به …»؛ اگر کد ناشناخته بود همان کد نمایش داده می‌شود.
    const currencyNames = {
        "USD": "دالر",
        "RUB": "روبل",
        "EUR": "یورو",
        "AED": "درهم",
        "AFN": "افغانی",
        "PKR": "کلدار",
        "TRY": "لیره",
        "CNY": "یوان"
    };

    function currencyDisplayName(code) {
        const key = (code || "").trim().toUpperCase();
        return currencyNames[key] || key || "ارز";
    }
    const currentTimeField = document.getElementById("journalCurrentTime");
    const submitButton = document.getElementById("journalSubmitButton");
    const submitButtonText = submitButton ? submitButton.querySelector("span") : null;
    const directionChoices = Array.from(document.querySelectorAll("[data-direction-choice]"));
    const counterpartyChoices = Array.from(document.querySelectorAll("[data-counterparty-choice]"));
    const panels = Array.from(document.querySelectorAll("[data-counterparty-panel]"));
    const supplierSelect = document.getElementById("supplierSelect") || document.getElementById("SupplierId");
    const contractSelect = document.getElementById("contractSelect") || document.getElementById("ContractId");
    const amountInput = document.getElementById("Amount");
    const totalDisplay = document.getElementById("journalTotalDisplay");
    const supplierPaymentContext = document.getElementById("supplierPaymentContext");

    // نمایش مانده‌ی طرف حساب (طلب/بدهی) — فقط‌خواندنی از همان منطق صورت‌حساب رسمی.
    const partyBalanceUrl = journalForm ? journalForm.getAttribute("data-party-balance-url") : null;
    const partyBalancePanel = document.getElementById("journalPartyBalance");
    const customerSelect = document.getElementById("CustomerId");
    const cashSarrafSelect = document.getElementById("cashSarrafSelect");
    const serviceProviderSelect = document.getElementById("ServiceProviderId");
    const employeeSelect = document.getElementById("EmployeeId");
    const supplierContextName = supplierPaymentContext ? supplierPaymentContext.querySelector("[data-supplier-context-name]") : null;
    const supplierContextContract = supplierPaymentContext ? supplierPaymentContext.querySelector("[data-supplier-context-contract]") : null;
    const supplierContextCurrency = supplierPaymentContext ? supplierPaymentContext.querySelector("[data-supplier-context-currency]") : null;
    const supplierContextAmount = supplierPaymentContext ? supplierPaymentContext.querySelector("[data-supplier-context-amount]") : null;

    if (!directionSelect || !counterpartySelect || !paymentKindSelect) {
        return;
    }

    const counterpartyTokens = {
        "0": "other",
        "1": "supplier",
        "2": "customer",
        "3": "employee",
        "4": "driver",
        "5": "officeexpense",
        "6": "contract",
        "7": "sales",
        "8": "shipment",
        "9": "serviceprovider",
        "10": "sarraf"
    };

    const defaultKinds = {
        "0": { "1": "6", "2": "5" },
        "1": { "1": "9", "2": "2" },
        "2": { "1": "1", "2": "10" },
        "3": { "1": "11", "2": "7" },
        "4": { "1": "6", "2": "4" },
        "5": { "1": "6", "2": "3" },
        "6": { "1": "6", "2": "5" },
        "7": { "1": "1", "2": "10" },
        "8": { "1": "6", "2": "4" },
        "9": { "1": "6", "2": "12" },
        "10": { "1": "6", "2": "5" }
    };

    const kindDirections = {
        "1": "1",
        "2": "2",
        "3": "2",
        "4": "2",
        "5": "2",
        "6": "1",
        "7": "2",
        "8": "2",
        "9": "1",
        "10": "2",
        "11": "1",
        "12": "2"
    };

    const kindCounterparties = {
        "1": "2",
        "2": "1",
        "3": "5",
        "4": "4",
        "5": "0",
        "6": "0",
        "7": "3",
        "8": "3",
        "9": "1",
        "10": "2",
        "11": "3",
        "12": "9"
    };

    let syncing = false;
    const supplierKindValues = new Set(["2", "9"]);

    function setSelectValue(select, value) {
        if (!select || !value) {
            return false;
        }

        const hasValue = Array.from(select.options).some(option => option.value === value);
        if (!hasValue) {
            return false;
        }

        select.value = value;
        return true;
    }

    function syncPanels() {
        const token = counterpartyTokens[counterpartySelect.value] || "other";

        panels.forEach(panel => {
            const visible = panel.dataset.counterpartyPanel
                .split(/\s+/)
                .filter(Boolean)
                .includes(token);

            panel.classList.toggle("d-none", !visible);
            panel.querySelectorAll("select, input, textarea").forEach(input => {
                input.disabled = !visible;
            });
        });
    }

    function isSupplierMode() {
        return counterpartySelect.value === "1" || supplierKindValues.has(paymentKindSelect.value);
    }

    function selectedOptionText(select) {
        if (!select || select.selectedIndex < 0) {
            return "";
        }

        return (select.options[select.selectedIndex].textContent || "").trim();
    }

    function syncSupplierContracts() {
        if (!contractSelect) {
            return;
        }

        const supplierMode = isSupplierMode();
        const supplierId = supplierSelect ? supplierSelect.value : "";

        Array.from(contractSelect.options).forEach(option => {
            if (!option.value) {
                option.hidden = false;
                option.disabled = false;
                return;
            }

            const belongsToSupplier = option.dataset.supplierId === supplierId;
            const isPurchase = option.dataset.contractType === "Purchase";
            const visible = !supplierMode || (Boolean(supplierId) && isPurchase && belongsToSupplier);

            option.hidden = !visible;
            option.disabled = !visible;
        });

        const selected = contractSelect.options[contractSelect.selectedIndex];
        if (selected && selected.disabled) {
            contractSelect.value = "";
        }
    }

    function syncSupplierPaymentContext() {
        if (!supplierPaymentContext) {
            return;
        }

        const visible = isSupplierMode() && supplierSelect && Boolean(supplierSelect.value);
        supplierPaymentContext.classList.toggle("d-none", !visible);

        if (!visible) {
            return;
        }

        if (supplierContextName) {
            supplierContextName.textContent = selectedOptionText(supplierSelect) || "—";
        }

        if (supplierContextContract) {
            supplierContextContract.textContent = selectedOptionText(contractSelect) || "—";
        }

        if (supplierContextCurrency && currencySelect) {
            supplierContextCurrency.textContent = currencySelect.value || "—";
        }

        if (supplierContextAmount && amountInput) {
            supplierContextAmount.textContent = amountInput.value || "0";
        }
    }

    function syncSupplierTools() {
        syncSupplierContracts();
        syncSupplierPaymentContext();
        syncPartyBalance();
    }

    // طرفِ انتخابی را به نوعِ مانده‌دار (مشتری=2 / تأمین‌کننده=1 / صراف=10) نگاشت می‌کند و مانده را می‌گیرد.
    let partyBalanceToken = 0;
    function syncPartyBalance() {
        if (!partyBalancePanel || !partyBalanceUrl) {
            return;
        }

        const type = counterpartySelect.value;
        let idSelect = null;
        if (type === "2") { idSelect = customerSelect; }
        else if (type === "1") { idSelect = supplierSelect; }
        else if (type === "10") { idSelect = cashSarrafSelect; }
        else if (type === "9") { idSelect = serviceProviderSelect; }
        else if (type === "3") { idSelect = employeeSelect; }

        const id = idSelect && idSelect.value ? idSelect.value : "";
        if (!id) {
            partyBalancePanel.classList.add("d-none");
            return;
        }

        const reqToken = ++partyBalanceToken;
        fetch(partyBalanceUrl + "?type=" + encodeURIComponent(type) + "&id=" + encodeURIComponent(id), {
            headers: { "X-Requested-With": "XMLHttpRequest" }
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (reqToken !== partyBalanceToken) {
                    return; // فقط آخرین درخواست اعمال می‌شود
                }
                if (!data || !data.available) {
                    partyBalancePanel.classList.add("d-none");
                    return;
                }
                const nameEl = partyBalancePanel.querySelector(".jpb-name");
                const amountEl = partyBalancePanel.querySelector(".jpb-amount");
                const labelEl = partyBalancePanel.querySelector(".jpb-label");
                if (nameEl) { nameEl.textContent = data.name || ""; }
                if (amountEl) { amountEl.textContent = data.amountText || ""; }
                if (labelEl) { labelEl.textContent = data.label || ""; }
                partyBalancePanel.classList.remove("d-none", "is-positive", "is-negative", "is-zero");
                partyBalancePanel.classList.add("is-" + (data.tone || "zero"));
            })
            .catch(function () {
                if (reqToken === partyBalanceToken) {
                    partyBalancePanel.classList.add("d-none");
                }
            });
    }

    // نمایش «مبلغ کل» در نوار پایین: مبلغِ واردشده + ارزِ انتخابی.
    function syncTotalDisplay() {
        if (!totalDisplay) {
            return;
        }

        const currency = (currencySelect && currencySelect.value ? currencySelect.value : "USD").toUpperCase();
        const raw = amountInput ? Number(amountInput.value) : 0;
        const amount = Number.isFinite(raw) ? raw : 0;
        totalDisplay.textContent = currency + " " + amount.toLocaleString(undefined, {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        });
    }

    function syncChoiceButtons() {
        directionChoices.forEach(choice => {
            const active = choice.dataset.directionValue === directionSelect.value;
            choice.classList.toggle("is-active", active);
            choice.setAttribute("aria-pressed", active ? "true" : "false");
        });

        counterpartyChoices.forEach(choice => {
            const active = choice.dataset.counterpartyValue === counterpartySelect.value;
            choice.classList.toggle("is-active", active);
            choice.setAttribute("aria-pressed", active ? "true" : "false");
        });
    }

    function syncFormTone() {
        const isReceipt = directionSelect.value === "1";
        const counterpartyToken = counterpartyTokens[counterpartySelect.value] || "other";

        if (journalForm) {
            journalForm.dataset.directionMode = isReceipt ? "in" : "out";
            journalForm.dataset.counterpartyMode = counterpartyToken;
        }

        if (submitButtonText && submitButton) {
            submitButtonText.textContent = isReceipt
                ? submitButton.dataset.submitTextReceipt || "ثبت دریافت"
                : submitButton.dataset.submitTextPayment || "ثبت پرداخت";
        }
    }

    function syncCurrentTime() {
        if (!currentTimeField) {
            return;
        }

        currentTimeField.value = new Date().toLocaleTimeString("fa-IR", {
            hour: "2-digit",
            minute: "2-digit"
        });
    }

    function applyDefaultKind() {
        const byDirection = defaultKinds[counterpartySelect.value] || defaultKinds["0"];
        const nextKind = byDirection[directionSelect.value] || byDirection["2"] || byDirection["1"];
        setSelectValue(paymentKindSelect, nextKind);
    }

    function syncDirectionAndCounterpartyFromKind() {
        const kind = paymentKindSelect.value;

        syncing = true;
        setSelectValue(directionSelect, kindDirections[kind]);
        setSelectValue(counterpartySelect, kindCounterparties[kind]);
        syncing = false;

        syncPanels();
        syncChoiceButtons();
        syncFormTone();
        syncSupplierTools();
    }

    function syncCurrencyFromCashAccount() {
        if (!cashAccountSelect || !currencySelect) {
            return;
        }

        const selected = cashAccountSelect.options[cashAccountSelect.selectedIndex];
        const accountCurrency = selected ? selected.dataset.currency : "";
        if (accountCurrency) {
            setSelectValue(currencySelect, accountCurrency);
        }

        syncFxVisibility();
        syncSupplierPaymentContext();
        syncTotalDisplay();
    }

    function syncFxVisibility() {
        if (!currencySelect) {
            return;
        }

        const currencyCode = (currencySelect.value || "").toUpperCase();
        const isUsd = currencyCode === "USD" || currencyCode === "";

        // برای USD نرخ تبدیل لازم نیست؛ فقط پیام راهنما نشان داده می‌شود.
        if (fxDocGroup) {
            fxDocGroup.classList.toggle("d-none", isUsd);
        }
        if (usdNote) {
            usdNote.classList.toggle("d-none", !isUsd);
        }

        // نام ارز در برچسب «نرخ دالر به …» را به‌روز کن.
        const displayName = currencyDisplayName(currencyCode);
        fxCurrencyNameTargets.forEach(target => {
            target.textContent = displayName;
        });

        if (fxInput) {
            if (isUsd) {
                // USD: فیلد نرخ خالی و غیرالزامی می‌شود تا سد ثبت نشود.
                fxInput.value = "";
                fxInput.required = false;
                fxInput.removeAttribute("required");
            } else {
                // غیر USD: نرخ باید بزرگ‌تر از صفر باشد.
                fxInput.required = true;
                fxInput.setAttribute("required", "required");
            }
        }
    }

    function normalizePaymentMethod(value) {
        const raw = String(value || "").trim().toLowerCase();
        if (raw === "1" || raw === "viasarraf" || raw === "via-sarraf" || raw === "sarraf") {
            return 1;
        }
        return 0;
    }

    // ----- Payment method handling -----
    function setPaymentMethodUI(methodVal) {
        const method = normalizePaymentMethod(methodVal); // 0 = CashBank, 1 = ViaSarraf

        if (paymentMethodInput) {
            paymentMethodInput.value = String(method);
        }

        paymentMethodChoices.forEach(choice => {
            const val = Number(choice.dataset.paymentMethodValue) || 0;
            const active = val === method;
            choice.classList.toggle('is-active', active);
            choice.setAttribute('aria-pressed', active ? 'true' : 'false');
        });

        // کنترل‌هایی که بیرون از fieldset نقد/بانک نشسته‌اند ولی فقط به همان مسیر تعلق دارند
        // (رادیوهای FundingSource). در حالت صراف disable می‌شوند تا مثل قبل post نشوند.
        cashbankOnlyControls.forEach(control => {
            control.disabled = method === 1;
        });

        // Cash/Bank sections
        cashbankSections.forEach(section => {
            if (method === 1) {
                section.classList.add('d-none');
                try { section.disabled = true; } catch (e) { section.setAttribute('disabled', 'disabled'); }
            } else {
                section.classList.remove('d-none');
                try { section.disabled = false; } catch (e) { section.removeAttribute('disabled'); }
            }
        });

        // Sarraf section
        if (sarrafSection) {
            const controls = sarrafSection.querySelectorAll('input,select,textarea');
            if (method === 1) {
                sarrafSection.classList.remove('d-none');
                controls.forEach(c => { c.disabled = false; });

                // فیلدهای همیشه‌الزامی مسیر صراف (مستقل از طرف‌حساب).
                // فیلد طرف‌حساب (تأمین‌کننده/مشتری/شرکت خدماتی/راننده/کارمند) در applySarrafCounterparty الزامی می‌شود.
                ['SarrafId', 'SarrafSupplierAmount', 'SarrafSupplierCurrency'].forEach(name => {
                    const el = sarrafSection.querySelector('[name="' + name + '"]');
                    if (el) {
                        el.required = true;
                        el.setAttribute('required', 'required');
                    }
                });

                // پرداختِ صراف عملاً روبلی است؛ اگر ارز خالی بود RUB را پیش‌فرض کن
                // تا مبلغِ روبلی به‌اشتباه به‌عنوان USD ثبت نشود (حساب تأمین‌کننده دقیقِ روبل بماند).
                if (sarrafCurrencySelect && !sarrafCurrencySelect.value) {
                    setSelectValue(sarrafCurrencySelect, 'RUB');
                }

                // جهت/نوع طرف‌حساب صراف را از مقدار سرور (یا پیش‌فرض Out) بردار؛
                // applySarrafDirection نوع فعلی را اگر با جهت هم‌خوان باشد حفظ می‌کند.
                // SARRAF_DIR_OUT/IN دقیقاً برابر مقادیر PaymentDirection.Out/In است، پس برچسب دکمهٔ ثبت هم درست می‌شود.
                const initialSarrafDir = (sarrafDirectionInput && sarrafDirectionInput.value)
                    ? sarrafDirectionInput.value
                    : SARRAF_DIR_OUT;
                setSelectValue(directionSelect, initialSarrafDir);
                syncChoiceButtons();
                syncFormTone();
                applySarrafDirection(initialSarrafDir, false);

                // ensure fx-rate fields for sarraf are refreshed
                if (window.PTG && typeof window.PTG.refreshFxRateFields === 'function') {
                    window.PTG.refreshFxRateFields();
                }
            } else {
                sarrafSection.classList.add('d-none');
                controls.forEach(c => { c.disabled = true; c.removeAttribute('required'); });
            }
        }

        syncSarrafSummary();
    }

    function setSarrafFieldRequired(el, required) {
        if (!el) { return; }
        if (required) { el.required = true; el.setAttribute('required', 'required'); }
        else { el.required = false; el.removeAttribute('required'); }
    }

    // نمایش/الزام فیلد طرف‌حساب بر اساس نوع (1=تأمین‌کننده، 2=مشتری، 3=شرکت خدماتی).
    function applySarrafCounterparty(typeVal) {
        const type = String(typeVal || "1");
        sarrafPartyPanels.forEach(panel => {
            const match = panel.dataset.sarrafParty === type;
            panel.hidden = !match;
            const field = panel.querySelector('select,input');
            if (field) {
                setSarrafFieldRequired(field, match);
                if (!match) { field.value = ""; }
            }
        });
        // بخش «تخصیص به قرارداد خرید» فقط برای تأمین‌کننده معنا دارد.
        if (sarrafAllocSection) {
            const isSupplier = type === "1";
            sarrafAllocSection.hidden = !isSupplier;
            if (!isSupplier) { setSarrafAllocMode("hold", false); }
        }
        syncSarrafSummary();
    }

    // تنظیم جهت صراف (پرداخت/دریافت) + برچسب/راهنمای مبلغ.
    // جهت و نوع طرف‌حساب مستقل‌اند؛ هر نوع در هر جهت مجاز است (سمت دفتر کل سمت سرور از جهت تعیین می‌شود).
    function applySarrafDirection(dirVal, userInitiated) {
        const dir = String(dirVal) === SARRAF_DIR_IN ? SARRAF_DIR_IN : SARRAF_DIR_OUT;
        if (sarrafDirectionInput) { sarrafDirectionInput.value = dir; }
        sarrafDirectionChoices.forEach(choice => {
            const active = String(choice.dataset.sarrafDirectionValue) === dir;
            choice.classList.toggle('is-active', active);
            choice.setAttribute('aria-pressed', active ? 'true' : 'false');
        });

        if (sarrafHintOut) { sarrafHintOut.hidden = dir !== SARRAF_DIR_OUT; }
        if (sarrafHintIn) { sarrafHintIn.hidden = dir !== SARRAF_DIR_IN; }
        if (sarrafAmountLabel) { sarrafAmountLabel.textContent = dir === SARRAF_DIR_IN ? "مبلغ دریافت‌شده" : "مبلغ فرستاده‌شده"; }

        // برچسب دکمهٔ ثبت (پرداخت/دریافت) با جهت صراف هم‌سو شود.
        if (setSelectValue(directionSelect, dir)) {
            syncChoiceButtons();
            syncFormTone();
        }

        applySarrafCounterparty(sarrafCounterpartyTypeSelect ? sarrafCounterpartyTypeSelect.value : "1");
    }

    function formatMoney(value, currency) {
        return (currency || "USD") + " " + value.toLocaleString(undefined, {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        });
    }

    function setAllText(list, text) {
        list.forEach(function (el) { el.textContent = text; });
    }

    function showSarrafSummaryVariant(mode) {
        if (sarrafSummaryHold) { sarrafSummaryHold.hidden = false; }
        if (sarrafSummaryContract) { sarrafSummaryContract.hidden = true; }
        sarrafSummaryPanel.hidden = false;
    }

    function syncSarrafSummary() {
        if (!sarrafSummaryPanel) {
            return;
        }

        const method = paymentMethodInput ? normalizePaymentMethod(paymentMethodInput.value) : 0;
        const amount = sarrafSupplierAmountInput ? Number(sarrafSupplierAmountInput.value) : 0;
        const currency = (sarrafCurrencySelect && sarrafCurrencySelect.value ? sarrafCurrencySelect.value : "").toUpperCase();
        const isUsd = currency === "USD";
        const rate = isUsd ? 1 : (sarrafSupplierRateInput ? Number(sarrafSupplierRateInput.value) : 0);

        const baseReady = method === 1
            && Number.isFinite(amount) && amount > 0
            && (currency === "USD" || currency === "RUB")
            && Number.isFinite(rate) && rate > 0;

        if (sarrafRateField) {
            sarrafRateField.hidden = isUsd;
        }
        if (sarrafCompanyRateField) {
            sarrafCompanyRateField.hidden = isUsd;
        }
        if (sarrafSupplierRateInput) {
            if (isUsd) {
                sarrafSupplierRateInput.required = false;
                sarrafSupplierRateInput.removeAttribute("required");
            } else {
                sarrafSupplierRateInput.required = true;
                sarrafSupplierRateInput.setAttribute("required", "required");
            }
        }

        if (!baseReady) {
            sarrafSummaryPanel.hidden = true;
            return;
        }

        // نرخ خرید ارز توسط شرکت اختیاری است. اگر خالی یا برابر نرخ طرف حساب باشد، همان حالت
        // تک‌نرخیِ قبلی است و تفاوتی ثبت نمی‌شود.
        const companyRateRaw = (!isUsd && sarrafCompanyRateInput) ? Number(sarrafCompanyRateInput.value) : 0;
        const companyRate = Number.isFinite(companyRateRaw) && companyRateRaw > 0 ? companyRateRaw : rate;
        const acceptedUsd = amount / rate;
        const companyCostUsd = amount / companyRate;
        // نتیجه از دید شرکت: قبول‌شده منهای هزینهٔ واقعی. مثبت = سود، منفی = ضرر.
        const fxResultUsd = acceptedUsd - companyCostUsd;
        const hasFxDifference = companyRate !== rate && Math.abs(fxResultUsd) >= 0.005;
        const isGain = fxResultUsd > 0;

        const display = formatMoney(amount, currency);
        const acceptedUsdDisplay = formatMoney(acceptedUsd, "USD");
        const companyUsdDisplay = formatMoney(companyCostUsd, "USD");
        const differenceDisplay = formatMoney(Math.abs(fxResultUsd), "USD");
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-supplier-amount]")), display);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-payable-amount]")), display);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-supplier-usd]")), acceptedUsdDisplay);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-payable-usd]")), companyUsdDisplay);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-usd]")), differenceDisplay);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-amount]")), differenceDisplay);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-accepted]")), acceptedUsdDisplay);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-company]")), companyUsdDisplay);

        const resultLabel = hasFxDifference
            ? (isGain ? "سود تفاوت نرخ ارز" : "ضرر تفاوت نرخ ارز")
            : "بدون تفاوت نرخ";
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-label]")), resultLabel);
        setAllText(Array.from(document.querySelectorAll("[data-sarraf-fxdiff-title]")), resultLabel + ".");
        setAllText(
            Array.from(document.querySelectorAll("[data-sarraf-fxdiff-hint]")),
            isGain ? "به‌عنوان درآمد ثبت می‌شود؛ صندوق زیاد نمی‌شود." : "به‌عنوان مصرف ثبت می‌شود؛ صندوق کم نمی‌شود.");
        setAllText(
            Array.from(document.querySelectorAll("[data-sarraf-fxdiff-effect]")),
            isGain
                ? "این مبلغ به‌عنوان درآمد در سود و زیان ثبت می‌شود."
                : "این مبلغ به‌عنوان مصرف در سود و زیان ثبت می‌شود.");

        if (sarrafFxDiffField) { sarrafFxDiffField.hidden = !hasFxDifference; }
        if (sarrafSingleRateNote) { sarrafSingleRateNote.hidden = hasFxDifference; }
        if (sarrafDualRateNote) { sarrafDualRateNote.hidden = !hasFxDifference; }
        showSarrafSummaryVariant("hold");
    }

    // تغییر حالت تخصیص؛ فقط نمایش فیلدها و خلاصه را عوض می‌کند، نگاشت کنترلر دست‌نخورده است.
    function setSarrafAllocMode(mode, userInitiated) {
        sarrafAllocMode = mode === "contract" ? "contract" : "hold";

        sarrafAllocChoices.forEach(function (choice) {
            const active = choice.dataset.sarrafAllocValue === sarrafAllocMode;
            choice.classList.toggle("is-active", active);
            choice.setAttribute("aria-pressed", active ? "true" : "false");
        });

        if (sarrafContractFields) {
            sarrafContractFields.hidden = sarrafAllocMode !== "contract";
        }

        if (sarrafAllocMode === "hold") {
            // بدون تخصیص: قرارداد پاک می‌شود تا مبلغ به‌عنوان اعتبار نزد تأمین‌کننده بماند.
            if (sarrafContractSelect) { sarrafContractSelect.value = ""; }
        }

        syncSarrafSummary();
    }

    [sarrafSupplierAmountInput, sarrafSupplierRateInput, sarrafCompanyRateInput].forEach(el => {
        if (el) {
            el.addEventListener("input", syncSarrafSummary);
            el.addEventListener("change", syncSarrafSummary);
        }
    });
    if (sarrafCurrencySelect) {
        sarrafCurrencySelect.addEventListener("change", syncSarrafSummary);
    }

    // دکمه‌های جهت صراف (صراف پرداخت کرد / دریافت کرد).
    sarrafDirectionChoices.forEach(function (choice) {
        choice.addEventListener("click", function () {
            applySarrafDirection(choice.dataset.sarrafDirectionValue, true);
        });
    });
    // تغییر نوع طرف‌حساب توسط کاربر.
    if (sarrafCounterpartyTypeSelect) {
        sarrafCounterpartyTypeSelect.addEventListener("change", function () {
            applySarrafCounterparty(sarrafCounterpartyTypeSelect.value);
        });
    }

    // دکمه‌های تخصیص به قرارداد (UI)
    sarrafAllocChoices.forEach(function (choice) {
        choice.addEventListener("click", function () {
            setSarrafAllocMode(choice.dataset.sarrafAllocValue, true);
        });
    });

    // حالت اولیه: اگر قراردادی از قبل انتخاب شده، حالت تخصیص؛ وگرنه «نزد تأمین‌کننده بماند».
    if (sarrafAllocChoices.length) {
        const initialAlloc = (sarrafContractSelect && sarrafContractSelect.value) ? "contract" : "hold";
        setSarrafAllocMode(initialAlloc, false);
    }

    // hookup choice buttons
    paymentMethodChoices.forEach(choice => {
        choice.addEventListener('click', function () {
            const val = choice.dataset.paymentMethodValue;
            setPaymentMethodUI(val);
        });
    });

    // initialize payment method UI from hidden input or server-rendered active class
    (function initPaymentMethodFromDom() {
        let initial = '0';
        if (paymentMethodInput && paymentMethodInput.value) {
            initial = paymentMethodInput.value;
        } else {
            const active = paymentMethodChoices.find(c => c.classList.contains('is-active'));
            if (active) { initial = active.dataset.paymentMethodValue || '0'; }
        }
        setPaymentMethodUI(initial);
    })();

    directionSelect.addEventListener("change", function () {
        if (syncing) {
            return;
        }

        applyDefaultKind();
        syncPanels();
        syncChoiceButtons();
        syncFormTone();
        syncSupplierTools();
    });

    counterpartySelect.addEventListener("change", function () {
        if (syncing) {
            return;
        }

        applyDefaultKind();
        syncPanels();
        syncChoiceButtons();
        syncFormTone();
        syncSupplierTools();
    });

    paymentKindSelect.addEventListener("change", syncDirectionAndCounterpartyFromKind);

    directionChoices.forEach(choice => {
        choice.addEventListener("click", function () {
            if (!setSelectValue(directionSelect, choice.dataset.directionValue)) {
                return;
            }

            // در حالت صرافی، جهت سند همان جهت حواله صراف است (کلید جداگانه حذف شده).
            if (sarrafSection && !sarrafSection.classList.contains("d-none")) {
                applySarrafDirection(choice.dataset.directionValue, true);
                return;
            }

            applyDefaultKind();
            syncPanels();
            syncChoiceButtons();
            syncFormTone();
            syncSupplierTools();
        });
    });

    counterpartyChoices.forEach(choice => {
        choice.addEventListener("click", function () {
            if (!setSelectValue(counterpartySelect, choice.dataset.counterpartyValue)) {
                return;
            }

            applyDefaultKind();
            syncPanels();
            syncChoiceButtons();
            syncFormTone();
            syncSupplierTools();
        });
    });

    if (cashAccountSelect) {
        cashAccountSelect.addEventListener("change", syncCurrencyFromCashAccount);
    }

    if (currencySelect) {
        currencySelect.addEventListener("change", function () {
            syncFxVisibility();
            syncSupplierPaymentContext();
            syncTotalDisplay();
        });
    }

    if (supplierSelect) {
        supplierSelect.addEventListener("change", syncSupplierTools);
    }

    if (customerSelect) {
        customerSelect.addEventListener("change", syncPartyBalance);
    }

    if (cashSarrafSelect) {
        cashSarrafSelect.addEventListener("change", syncPartyBalance);
    }

    if (serviceProviderSelect) {
        serviceProviderSelect.addEventListener("change", syncPartyBalance);
    }

    if (employeeSelect) {
        employeeSelect.addEventListener("change", syncPartyBalance);
    }

    if (contractSelect) {
        contractSelect.addEventListener("change", syncSupplierPaymentContext);
    }

    if (amountInput) {
        amountInput.addEventListener("input", function () {
            syncSupplierPaymentContext();
            syncTotalDisplay();
        });
        amountInput.addEventListener("change", function () {
            syncSupplierPaymentContext();
            syncTotalDisplay();
        });
    }

    if (journalForm) {
        journalForm.addEventListener("reset", function () {
            window.setTimeout(function () {
                syncPanels();
                syncChoiceButtons();
                syncCurrencyFromCashAccount();
                syncFormTone();
                syncSupplierTools();
            }, 0);
        });
    }

    syncCurrentTime();
    if (currentTimeField) {
        window.PTG.roznamchaTimeInterval = window.setInterval(syncCurrentTime, 60000);
    }

    syncPanels();
    syncChoiceButtons();
    syncFormTone();
    syncFxVisibility();
    syncSupplierTools();
    syncTotalDisplay();
})();
