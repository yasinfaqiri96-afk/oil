(function () {
    "use strict";

    var tolerance = 0.0001;

    function number(value) {
        var parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : 0;
    }

    function fixed(value, digits) {
        return number(value).toFixed(digits == null ? 4 : digits);
    }

    function readableQuantity(value) {
        return number(value).toLocaleString("en-US", {
            minimumFractionDigits: 4,
            maximumFractionDigits: 4
        });
    }

    function escapeHtml(value) {
        return String(value == null ? "" : value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    function initialize(page) {
        var form = page.querySelector("[data-inv-transport-form]");
        if (!form || form.dataset.ready === "true") return;
        form.dataset.ready = "true";

        var sourceRows = form.querySelector("[data-source-rows]");
        var vehicleRows = form.querySelector("[data-vehicle-rows]");
        var dialog = page.querySelector("[data-allocation-dialog]");
        var activeAllocationRow = null;

        function rows() {
            return Array.from(vehicleRows.querySelectorAll("[data-vehicle-row]"));
        }

        // منابع تیک‌خورده = «حوض» موجودی که سیستم از آن کسر می‌کند.
        function pooledRows() {
            return Array.from(sourceRows.querySelectorAll("[data-source-row]"))
                .filter(function (row) {
                    var check = row.querySelector("[data-source-check]");
                    return check && check.checked;
                });
        }

        function selectedSources() {
            return pooledRows()
                .map(function (row) {
                    return {
                        id: Number(row.dataset.sourceId),
                        label: row.dataset.label || "",
                        available: number(row.dataset.available),
                        quantity: number(row.querySelector("[data-source-quantity]").value)
                    };
                })
                .filter(function (source) { return source.quantity > 0; });
        }

        function readPostedAllocations(row) {
            var fields = row.querySelector("[data-allocation-fields]");
            var ids = Array.from(fields.querySelectorAll("input[name$='.SourceInventoryMovementId']"));
            var quantities = Array.from(fields.querySelectorAll("input[name$='.QuantityMt']"));
            var result = {};
            ids.forEach(function (field, index) {
                var quantity = quantities[index] ? number(quantities[index].value) : 0;
                if (quantity > 0) result[Number(field.value)] = quantity;
            });
            row._allocations = result;
        }

        function syncAllocationFields(row) {
            var index = Number(row.dataset.index);
            var fields = row.querySelector("[data-allocation-fields]");
            var sources = selectedSources();
            var parts = [];
            fields.innerHTML = "";
            Object.keys(row._allocations || {}).forEach(function (sourceId, allocationIndex) {
                var quantity = number(row._allocations[sourceId]);
                if (quantity <= 0) return;
                var source = sources.find(function (item) { return item.id === Number(sourceId); });
                var idInput = document.createElement("input");
                idInput.type = "hidden";
                idInput.name = "Vehicles[" + index + "].Allocations[" + allocationIndex + "].SourceInventoryMovementId";
                idInput.value = sourceId;
                fields.appendChild(idInput);
                var quantityInput = document.createElement("input");
                quantityInput.type = "hidden";
                quantityInput.name = "Vehicles[" + index + "].Allocations[" + allocationIndex + "].QuantityMt";
                quantityInput.value = fixed(quantity);
                fields.appendChild(quantityInput);
                parts.push((source ? source.label : "منبع #" + sourceId) + ": " + fixed(quantity));
            });
            var summary = row.querySelector("[data-allocation-summary]");
            if (summary) {
                summary.textContent = parts.length ? parts.join("، ") : "بدون سهم";
                summary.title = parts.join(" | ");
            }
        }

        function autoAllocate() {
            // حوض = منابع تیک‌خورده تا سقفِ «قابل حمل». جمع مقدار موترها به‌ترتیب (FIFO) بین
            // آن‌ها تقسیم می‌شود؛ کاربر فقط مقدار هر موتر را وارد می‌کند.
            var pool = pooledRows().map(function (row) {
                return { id: Number(row.dataset.sourceId), available: number(row.dataset.available), consumed: 0 };
            });
            rows().forEach(function (row) {
                var needed = number(row.querySelector("[data-vehicle-quantity]").value);
                row._allocations = {};
                pool.forEach(function (source) {
                    if (needed <= tolerance) return;
                    var free = source.available - source.consumed;
                    if (free <= tolerance) return;
                    var share = Math.min(needed, free);
                    row._allocations[source.id] = share;
                    source.consumed += share;
                    needed -= share;
                });
                syncAllocationFields(row);
            });
            // سهمِ کسرشدهٔ محاسبه‌شده را در فیلد مخفی و نمایشِ هر منبع بنویس (این همان مقداری است
            // که به سرور فرستاده می‌شود؛ پس جمع منابع = جمع موترها همیشه برقرار می‌ماند).
            Array.from(sourceRows.querySelectorAll("[data-source-row]")).forEach(function (row) {
                var quantityInput = row.querySelector("[data-source-quantity]");
                var consumedNode = row.querySelector("[data-source-consumed]");
                var match = pool.find(function (item) { return item.id === Number(row.dataset.sourceId); });
                var value = match ? match.consumed : 0;
                if (quantityInput) quantityInput.value = value > tolerance ? fixed(value) : "";
                if (consumedNode) consumedNode.textContent = fixed(value);
            });
            recalculate();
        }

        function updateFreightUsd(row) {
            var rateInput = row.querySelector("[data-freight-rate]");
            var freightInput = row.querySelector("[data-freight]");
            var usdNode = row.querySelector("[data-freight-usd]");
            if (!rateInput || !freightInput || !usdNode) return;
            var rate = number(rateInput.value);
            // کرایه بر مبنای «وزن محاسبه» است؛ اگر خالی بود، مقدار اصلی حمل مبنا می‌شود.
            var weightInput = row.querySelector("[data-freight-weight]");
            var freightWeight = weightInput ? number(weightInput.value) : 0;
            var quantity = freightWeight > 0 ? freightWeight : number(row.querySelector("[data-vehicle-quantity]").value);
            var total = rate * quantity;
            freightInput.value = total > 0 ? fixed(total, 2) : "";
            usdNode.textContent = total > 0 ? fixed(total, 2) : "—";
        }

        function updateVehicleRow(row) {
            var isTruck = row.querySelector("[data-vehicle-type]").value === "3";
            var driver = row.querySelector("[data-driver]");
            var carrier = row.querySelector("[data-carrier-type]").value;
            var provider = row.querySelector("[data-provider]");
            var asset = row.querySelector("[data-asset]");
            var assetVehicleNote = row.querySelector("[data-asset-vehicle-note]");
            // یک کمبوباکس در هر حالت: نمبر پلیت (موتر) یا نمبر واگن — هم انتخاب از لیست، هم تایپ جدید.
            var truckPlate = row.querySelector("[data-truck-plate]");
            var wagonNumber = row.querySelector("[data-wagon-number]");
            var usesProvider = carrier === "1";

            truckPlate.hidden = !usesProvider || !isTruck;
            truckPlate.disabled = !usesProvider || !isTruck;
            wagonNumber.hidden = !usesProvider || isTruck;
            wagonNumber.disabled = !usesProvider || isTruck;
            assetVehicleNote.hidden = usesProvider;
            driver.hidden = !isTruck;
            driver.disabled = !isTruck;
            if (!usesProvider) {
                truckPlate.value = "";
                wagonNumber.value = "";
            } else if (isTruck) {
                wagonNumber.value = "";
            } else {
                truckPlate.value = "";
                driver.value = "";
            }

            provider.hidden = !usesProvider;
            provider.disabled = !usesProvider;
            asset.hidden = usesProvider;
            asset.disabled = usesProvider;
            if (usesProvider) asset.value = "";
            else provider.value = "";

            Array.from(asset.options).forEach(function (option) {
                if (!option.value) return;
                var type = Number(option.dataset.assetType);
                option.hidden = isTruck ? type !== 1 && type !== 3 : type !== 7;
            });
            if (asset.selectedOptions[0] && asset.selectedOptions[0].hidden) asset.value = "";

            updateFreightUsd(row);
        }

        function renumberRows() {
            rows().forEach(function (row, index) {
                row.dataset.index = String(index);
                row.querySelectorAll("[name]").forEach(function (field) {
                    field.name = field.name.replace(/Vehicles\[\d+\]/, "Vehicles[" + index + "]");
                });
                syncAllocationFields(row);
            });
        }

        function recalculate() {
            var sources = selectedSources();
            var selectedTotal = sources.reduce(function (sum, source) { return sum + source.quantity; }, 0);
            var vehicleTotal = rows().reduce(function (sum, row) { return sum + number(row.querySelector("[data-vehicle-quantity]").value); }, 0);
            var difference = selectedTotal - vehicleTotal;
            var allocationsBySource = {};
            var allocationValid = true;
            rows().forEach(function (row) {
                var allocationTotal = 0;
                Object.keys(row._allocations || {}).forEach(function (sourceId) {
                    var quantity = number(row._allocations[sourceId]);
                    allocationTotal += quantity;
                    allocationsBySource[sourceId] = number(allocationsBySource[sourceId]) + quantity;
                });
                if (Math.abs(allocationTotal - number(row.querySelector("[data-vehicle-quantity]").value)) > tolerance) allocationValid = false;
            });
            sources.forEach(function (source) {
                if (Math.abs(number(allocationsBySource[source.id]) - source.quantity) > tolerance) allocationValid = false;
            });

            var freightTotal = rows().reduce(function (sum, row) {
                var field = row.querySelector("[data-freight]");
                return sum + (field ? number(field.value) : 0);
            }, 0);
            var freightText = freightTotal > 0 ? fixed(freightTotal, 2) + " USD" : "0.00";

            form.querySelector("[data-selected-total]").textContent = fixed(selectedTotal);
            form.querySelector("[data-summary-source]").textContent = fixed(selectedTotal) + " MT";
            form.querySelector("[data-summary-vehicles]").textContent = fixed(vehicleTotal) + " MT";
            var differenceNode = form.querySelector("[data-summary-difference]");
            differenceNode.textContent = fixed(difference) + " MT";
            differenceNode.classList.toggle("is-invalid", Math.abs(difference) > tolerance || !allocationValid);
            differenceNode.title = allocationValid ? "" : "جمع سهم منابع هر وسیله یا هر منبع کامل نیست.";
            form.querySelector("[data-summary-freight]").textContent = freightText;

            var canSubmit = selectedTotal > 0 && rows().length > 0 && Math.abs(difference) <= tolerance && allocationValid;
            var summaryMessage = form.querySelector("[data-summary-message]");
            if (summaryMessage) {
                var reason = null;
                if (rows().length === 0) {
                    reason = "حداقل یک وسیله حمل باید وارد شود.";
                } else if (vehicleTotal <= 0) {
                    reason = "مقدار حداقل یک موتر یا واگن را وارد کنید.";
                } else if (Math.abs(difference) > tolerance || !allocationValid) {
                    reason = "موجودی منابع تیک‌خورده کمتر از جمع بارگیری موترهاست؛ منبع بیشتری تیک بزنید یا مقدار موترها را کم کنید.";
                }
                summaryMessage.textContent = reason || "";
                summaryMessage.hidden = !reason;
            }
            form.querySelectorAll("[data-submit-button]").forEach(function (button) { button.disabled = !canSubmit; });

            // «بار روی کشتی» فقط با «ثبت و بارگیری» مجاز است؛ اگر انتخاب شده باشد، پیش‌نویس قفل می‌شود.
            // منبع «بار روی کشتی» فقط با «ثبت و بارگیری» قابل حمل است؛ دکمهٔ پیش‌نویس پنهان و غیرفعال
            // می‌شود تا کاربر به‌اشتباه پیش‌نویس نزند و ارور نگیرد.
            var hasVesselSource = sources.some(function (source) { return source.id < 0; });
            var draftButton = form.querySelector("[data-submit-button][value=\"0\"]");
            if (draftButton) {
                draftButton.disabled = hasVesselSource || draftButton.disabled;
                draftButton.hidden = hasVesselSource;
                draftButton.title = hasVesselSource ? "بار روی کشتی فقط با «ثبت و بارگیری» قابل حمل است." : "";
            }
            var vesselHint = form.querySelector("[data-vessel-hint]");
            if (vesselHint) vesselHint.hidden = !hasVesselSource;

            var summarySection = form.querySelector("[data-summary-section]");
            if (summarySection) {
                summarySection.hidden = !(selectedTotal > 0 && rows().length > 0);
            }

            var allocSelected = form.querySelector("[data-alloc-selected]");
            var allocAssigned = form.querySelector("[data-alloc-assigned]");
            var allocRemaining = form.querySelector("[data-alloc-remaining]");
            if (allocSelected && allocAssigned && allocRemaining) {
                allocSelected.textContent = fixed(selectedTotal);
                allocAssigned.textContent = fixed(vehicleTotal);
                allocRemaining.textContent = fixed(difference);
                var remItem = allocRemaining.closest("[data-alloc-item]");
                if (remItem) {
                    remItem.classList.toggle("is-over", difference < -tolerance);
                    remItem.classList.toggle("is-done", Math.abs(difference) <= tolerance && selectedTotal > 0);
                }
            }
        }

        function setStep(step) {
            step = Math.max(1, Math.min(4, number(step) || 1));
            var activeStep = form.querySelector("[data-active-step]");
            if (activeStep) activeStep.value = String(step);
            form.querySelectorAll("[data-step]").forEach(function (section) {
                section.classList.toggle("is-active", Number(section.dataset.step) === step);
            });
            form.querySelectorAll("[data-step-target]").forEach(function (tab) {
                var target = Number(tab.dataset.stepTarget);
                tab.classList.toggle("is-active", target === step);
                tab.classList.toggle("is-done", target < step);
            });
        }

        function filterTanks() {
            var terminalEl = form.querySelector("[data-source-terminal]");
            var tank = form.querySelector("[data-source-tank]");
            // در حالتِ محموله (مبدأ = محموله) این selectها وجود ندارند؛ فیلترِ مخزن لازم نیست.
            if (!terminalEl || !tank) return;
            var terminal = terminalEl.value;
            var product = form.querySelector("[data-source-product]").value;
            Array.from(tank.options).forEach(function (option) {
                if (!option.value) return;
                var productMatches = !option.dataset.productId || option.dataset.productId === product;
                option.hidden = option.dataset.terminalId !== terminal || !productMatches;
            });
            if (tank.selectedOptions[0] && tank.selectedOptions[0].hidden) tank.value = "";
        }

        // حالت «مبدأ = محموله» با انتخاب از خود فرم: ترمینال/مخزن پنهان و خالی می‌شوند تا سرور
        // ترمینالِ عبور را از خودِ محموله استنتاج کند (همان رفتار ورود از پروندهٔ محموله).
        function applyShipmentMode() {
            var shipmentSelect = form.querySelector("select[data-shipment-id]");
            if (!shipmentSelect) return;
            var fromShipment = !!shipmentSelect.value;
            [["[data-source-terminal]", "[data-terminal-field]"], ["[data-source-tank]", "[data-tank-field]"]].forEach(function (pair) {
                var control = form.querySelector(pair[0]);
                var field = form.querySelector(pair[1]);
                if (field) field.hidden = fromShipment;
                if (!control) return;
                control.disabled = fromShipment;
                if (fromShipment) control.value = "";
            });
        }

        function renderSources(sources) {
            sourceRows.innerHTML = sources.map(function (source, index) {
                var kind = source.sourceKind || source.receiptReference || "";
                var isVessel = Number(source.sourceInventoryMovementId) < 0;
                var kindClass = "ak-status" + (isVessel ? " is-warning" : "");
                return "<tr data-source-row data-source-id=\"" + source.sourceInventoryMovementId + "\" data-available=\"" + source.availableQuantityMt + "\"" + (isVessel ? " data-source-vessel=\"true\"" : "") + " data-label=\"" + escapeHtml(source.contractNumber + " / " + kind) + "\">" +
                    "<td><input type=\"checkbox\" data-source-check></td>" +
                    "<td>" + escapeHtml(source.contractNumber) + "</td><td><span class=\"" + kindClass + "\">" + escapeHtml(kind) + "</span></td><td>" + escapeHtml(source.sourceDate) + "</td><td><strong class=\"ak-num\">" + readableQuantity(source.availableQuantityMt) + "</strong></td>" +
                    "<td><input type=\"hidden\" name=\"Sources[" + index + "].SourceInventoryMovementId\" value=\"" + source.sourceInventoryMovementId + "\"><input type=\"hidden\" name=\"Sources[" + index + "].QuantityMt\" data-source-quantity><span class=\"ak-num\" data-source-consumed>0.0000</span></td></tr>";
            }).join("");
            form.querySelector("[data-source-empty]").hidden = sources.length > 0;
            autoAllocate();
            syncSourceCheckAll();
        }

        function syncSourceCheckAll() {
            var all = form.querySelector("[data-source-check-all]");
            if (!all) return;
            var checks = Array.from(sourceRows.querySelectorAll("[data-source-check]"));
            all.checked = checks.length > 0 && checks.every(function (cb) { return cb.checked; });
            all.indeterminate = !all.checked && checks.some(function (cb) { return cb.checked; });
        }


        async function loadSources() {
            var terminalEl = form.querySelector("[data-source-terminal]");
            var tankEl = form.querySelector("[data-source-tank]");
            var product = form.querySelector("[data-source-product]").value;
            var shipmentId = form.querySelector("[data-shipment-id]");
            var fromShipment = !!(shipmentId && shipmentId.value);
            var terminal = terminalEl ? terminalEl.value : "";
            var tank = tankEl ? tankEl.value : "";
            // در حالتِ محموله فقط محصول لازم است؛ ترمینال/مخزنِ عبور در سرور از خودِ محموله استنتاج می‌شود.
            if (!product || (!fromShipment && (!terminal || !tank))) {
                renderSources([]);
                return;
            }
            var url = new URL(page.dataset.sourcesUrl, window.location.origin);
            if (terminal) url.searchParams.set("terminalId", terminal);
            if (tank) url.searchParams.set("storageTankId", tank);
            url.searchParams.set("productId", product);
            if (fromShipment) url.searchParams.set("shipmentId", shipmentId.value);
            try {
                var response = await fetch(url.toString(), { headers: { "X-Requested-With": "XMLHttpRequest" } });
                var data = await response.json();
                renderSources(data.ok && Array.isArray(data.sources) ? data.sources : []);
            } catch (_) {
                renderSources([]);
            }
        }

        function openAllocationDialog(row) {
            if (!dialog) return;
            activeAllocationRow = row;
            var editor = dialog.querySelector("[data-allocation-editor]");
            editor.innerHTML = selectedSources().map(function (source) {
                return "<label><span>" + escapeHtml(source.label) + "</span><input class=\"ak-input\" type=\"number\" min=\"0\" step=\"0.0001\" data-dialog-source=\"" + source.id + "\" value=\"" + fixed((row._allocations || {})[source.id]) + "\"></label>";
            }).join("");
            updateDialogTotal();
            dialog.showModal();
        }

        function updateDialogTotal() {
            if (!dialog) return;
            var total = Array.from(dialog.querySelectorAll("[data-dialog-source]")).reduce(function (sum, input) { return sum + number(input.value); }, 0);
            dialog.querySelector("[data-dialog-total]").textContent = fixed(total);
        }

        var freightToggle = form.querySelector("[data-freight-toggle]");
        var vehicleTable = form.querySelector("[data-vehicle-table]");
        function applyFreightVisibility() {
            if (!vehicleTable || !freightToggle) return;
            vehicleTable.querySelectorAll("[data-freight-col]").forEach(function (cell) {
                cell.hidden = !freightToggle.checked;
            });
        }
        if (freightToggle) freightToggle.addEventListener("change", applyFreightVisibility);
        applyFreightVisibility();

        rows().forEach(function (row) { readPostedAllocations(row); updateVehicleRow(row); syncAllocationFields(row); });
        renumberRows();
        filterTanks();
        applyShipmentMode();
        recalculate();
        syncSourceCheckAll();
        var postedActiveStep = form.querySelector("[data-active-step]");
        setStep(postedActiveStep ? postedActiveStep.value : 1);

        form.addEventListener("click", function (event) {
            var next = event.target.closest("[data-next-step]");
            var previous = event.target.closest("[data-prev-step]");
            var tab = event.target.closest("[data-step-target]");
            if (next) setStep(Math.min(4, Number(next.closest("[data-step]").dataset.step) + 1));
            if (previous) setStep(Math.max(1, Number(previous.closest("[data-step]").dataset.step) - 1));
            if (tab) setStep(Number(tab.dataset.stepTarget));

            var remove = event.target.closest("[data-remove-vehicle]");
            if (remove) {
                var currentRows = rows();
                if (currentRows.length > 1) remove.closest("[data-vehicle-row]").remove();
                else currentRows[0].querySelectorAll("input, select").forEach(function (field) { if (!field.hasAttribute("readonly") && field.type !== "checkbox") field.value = field.matches("[data-vehicle-type],[data-carrier-type]") ? field.options[0].value : ""; });
                renumberRows();
                autoAllocate();
            }
            if (event.target.closest("[data-edit-allocations]")) openAllocationDialog(event.target.closest("[data-vehicle-row]"));

            // تطبیق حمل‌کنندهٔ سطر اول (نوع حمل‌کننده + شرکت خدماتی/دارایی عملیاتی) به تمام سطرها.
            if (event.target.closest("[data-apply-carrier-all]")) {
                var allRows = rows();
                if (allRows.length > 1) {
                    var first = allRows[0];
                    var carrierType = first.querySelector("[data-carrier-type]").value;
                    var providerId = first.querySelector("[data-provider]").value;
                    var assetId = first.querySelector("[data-asset]").value;
                    allRows.slice(1).forEach(function (row) {
                        row.querySelector("[data-carrier-type]").value = carrierType;
                        row.querySelector("[data-provider]").value = carrierType === "1" ? providerId : "";
                        row.querySelector("[data-asset]").value = carrierType === "2" ? assetId : "";
                        updateVehicleRow(row);
                    });
                    recalculate();
                }
            }
        });

        function buildVehicleRow() {
            var template = page.parentElement.querySelector("[data-vehicle-template]") || document.querySelector("[data-vehicle-template]");
            var html = template.innerHTML.replace(/__index__/g, String(rows().length));
            vehicleRows.insertAdjacentHTML("beforeend", html);
            var row = rows()[rows().length - 1];
            row._allocations = {};
            return row;
        }

        form.querySelector("[data-add-vehicle]").addEventListener("click", function () {
            var row = buildVehicleRow();
            updateVehicleRow(row);
            renumberRows();
            autoAllocate();
        });

        function applyImportedVehicles(list) {
            rows().forEach(function (row) { row.remove(); });
            (list || []).forEach(function (item) {
                var row = buildVehicleRow();
                var typeSelect = row.querySelector("[data-vehicle-type]");
                typeSelect.value = String(item.transportType) === "2" ? "2" : "3";
                var isTruck = typeSelect.value === "3";
                var vehicleNumber = (item.vehicleNumber == null ? "" : String(item.vehicleNumber)).trim();
                if (isTruck) row.querySelector("[data-truck-plate]").value = vehicleNumber;
                else row.querySelector("[data-wagon-number]").value = vehicleNumber;
                var rwbInput = row.querySelector("[data-rwb]");
                if (rwbInput && item.rwbNo != null) rwbInput.value = String(item.rwbNo);
                row.querySelector("[data-vehicle-quantity]").value = item.quantityMt != null ? item.quantityMt : "";
                var weightInput = row.querySelector("[data-freight-weight]");
                if (weightInput) weightInput.value = item.freightWeightMt != null ? item.freightWeightMt : "";
                var rateInput = row.querySelector("[data-freight-rate]");
                // نرخ فی‌تن = کرایه ÷ وزن محاسبه (اگر نبود، ÷ مقدار حمل) — همسان با ساختار اکسل.
                var freightBase = number(item.freightWeightMt) > 0 ? number(item.freightWeightMt) : number(item.quantityMt);
                if (rateInput && item.freightAmount != null && freightBase > 0) {
                    rateInput.value = fixed(number(item.freightAmount) / freightBase, 4);
                }
                updateVehicleRow(row);
            });
            if (rows().length === 0) {
                updateVehicleRow(buildVehicleRow());
            }
            renumberRows();
            autoAllocate();
        }

        var importComponent = form.querySelector("[data-vehicle-excel-import] [data-excel-import]");
        if (importComponent) {
            importComponent.addEventListener("excel-import:start", async function (event) {
                var api = event.detail;
                var file = api.file;
                api.progress({ stage: 2, message: "در حال خواندن ستون‌ها و آماده‌سازی وسایط…" });
                var body = new FormData();
                body.append("file", file);
                var token = form.querySelector('input[name="__RequestVerificationToken"]');
                if (token) body.append("__RequestVerificationToken", token.value);
                try {
                    var response = await fetch(page.dataset.importUrl, {
                        method: "POST",
                        body: body,
                        headers: { "X-Requested-With": "XMLHttpRequest" }
                    });
                    var data = await response.json();
                    if (!data.ok) {
                        api.fail(data.message || "ورود فایل ناموفق بود.");
                    } else {
                        applyImportedVehicles(data.vehicles);
                        var count = data.vehicles ? data.vehicles.length : 0;
                        api.complete({
                            message: count + " وسیله به جدول افزوده شد؛ حمل‌کننده و راننده را بررسی کنید.",
                            createdRows: count,
                            rejectedRows: 0,
                            updatedRows: 0
                        });
                    }
                } catch (_) {
                    api.fail("خطا در ورود فایل اکسل.");
                }
            });
        }

        form.addEventListener("input", function (event) {
            if (event.target.matches("[data-vehicle-quantity]")) {
                updateFreightUsd(event.target.closest("[data-vehicle-row]"));
                autoAllocate();
            }
            else if (event.target.matches("[data-truck-plate],[data-wagon-number]")) {
                updateVehicleRow(event.target.closest("[data-vehicle-row]"));
                recalculate();
            } else if (event.target.matches("[data-freight-rate],[data-freight-weight]")) {
                updateFreightUsd(event.target.closest("[data-vehicle-row]"));
                recalculate();
            } else recalculate();
        });
        form.addEventListener("change", function (event) {
            if (event.target.matches("select[data-shipment-id]")) {
                applyShipmentMode();
                loadSources();
            } else if (event.target.matches("[data-source-terminal],[data-source-product]")) {
                filterTanks();
                loadSources();
            } else if (event.target.matches("[data-source-tank]")) {
                loadSources();
            } else if (event.target.matches("[data-source-check]")) {
                syncSourceCheckAll();
                autoAllocate();
            } else if (event.target.matches("[data-source-check-all]")) {
                var wantChecked = event.target.checked;
                Array.from(sourceRows.querySelectorAll("[data-source-row]")).forEach(function (row) {
                    var cb = row.querySelector("[data-source-check]");
                    if (cb) cb.checked = wantChecked;
                });
                autoAllocate();
            } else if (event.target.matches("[data-vehicle-type],[data-carrier-type],[data-asset]")) {
                updateVehicleRow(event.target.closest("[data-vehicle-row]"));
                recalculate();
            } else {
                recalculate();
            }
        });

        form.addEventListener("submit", function (event) {
            renumberRows();
            recalculate();
            setStep(4);
            if (form.dataset.submitting === "true"
                || Array.from(form.querySelectorAll("[data-submit-button]")).every(function (button) { return button.disabled; })) {
                event.preventDefault();
                return;
            }
            form.dataset.submitting = "true";
        });

        if (dialog) {
            dialog.addEventListener("input", updateDialogTotal);
            var saveAllocations = dialog.querySelector("[data-save-allocations]");
            if (saveAllocations) {
                saveAllocations.addEventListener("click", function () {
                    if (!activeAllocationRow) return;
                    activeAllocationRow._allocations = {};
                    dialog.querySelectorAll("[data-dialog-source]").forEach(function (input) {
                        if (number(input.value) > 0) activeAllocationRow._allocations[Number(input.dataset.dialogSource)] = number(input.value);
                    });
                    syncAllocationFields(activeAllocationRow);
                    dialog.close();
                    recalculate();
                });
            }
        }
    }

    function boot() {
        document.querySelectorAll("[data-inv-transport-form-page]").forEach(initialize);
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot, { once: true });
    else boot();
})();
