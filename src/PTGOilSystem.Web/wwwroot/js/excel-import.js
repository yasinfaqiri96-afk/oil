(function () {
    'use strict';

    const STATE = { processing: 1, ready: 2, registering: 3, completed: 4, failed: 5, cancelled: 6 };

    function init(root) {
        if (root.dataset.excelImportReady === 'true') return;
        root.dataset.excelImportReady = 'true';

        const form = root.closest('form');
        const fileInput = root.querySelector('[data-excel-file]');
        const dropzone = root.querySelector('[data-excel-dropzone]');
        const uploader = root.querySelector('[data-excel-uploader]');
        const validation = root.querySelector('[data-excel-validation]');
        const progress = root.querySelector('[data-excel-progress]');
        const result = root.querySelector('[data-excel-result]');
        const startButton = root.querySelector('[data-excel-start]');
        const mode = (root.dataset.mode || 'job').toLowerCase();
        const storageKey = `ptg-excel-job:${location.pathname}:${root.dataset.startUrl}`;
        let jobId = localStorage.getItem(storageKey);
        let pollTimer = 0;

        const urlFor = (template) => template.replace('{id}', jobId || '');
        const showOnly = (panel) => [uploader, validation, progress, result].forEach(item => item.classList.toggle('d-none', item !== panel));
        const tokenData = () => {
            const data = new FormData();
            const token = form?.querySelector('input[name="__RequestVerificationToken"]')?.value;
            if (token) data.append('__RequestVerificationToken', token);
            return data;
        };
        const humanSize = bytes => bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(0)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
        const humanTime = seconds => `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;

        function localError(message) {
            const box = root.querySelector('[data-excel-local-error]');
            box.textContent = message;
            box.classList.toggle('d-none', !message);
        }

        function selectFile(file) {
            localError('');
            if (!file) return reset();
            const accepted = (fileInput.accept || '.xlsx,.xls').split(',').map(value => value.trim().toLowerCase()).filter(Boolean);
            if (!accepted.some(extension => file.name.toLowerCase().endsWith(extension))) return localError(`فقط فایل‌های ${accepted.join(' و ')} قابل قبول است.`);
            if (file.size > Number(root.dataset.maxSize || 0)) return localError('حجم فایل بیشتر از حد مجاز است.');
            const transfer = new DataTransfer();
            transfer.items.add(file);
            fileInput.files = transfer.files;
            dropzone.classList.add('d-none');
            root.querySelector('[data-excel-file-card]').classList.remove('d-none');
            root.querySelector('[data-excel-file-name]').textContent = file.name;
            root.querySelector('[data-excel-file-size]').textContent = humanSize(file.size);
            root.querySelector('[data-excel-sheet-info]').textContent = '';
            startButton.disabled = false;
        }

        function reset() {
            clearTimeout(pollTimer);
            jobId = null;
            localStorage.removeItem(storageKey);
            fileInput.value = '';
            dropzone.classList.remove('d-none');
            root.querySelector('[data-excel-file-card]').classList.add('d-none');
            startButton.disabled = true;
            localError('');
            showOnly(uploader);
            root.dispatchEvent(new CustomEvent('excel-import:reset'));
        }

        function setSteps(stage) {
            root.querySelectorAll('[data-excel-step]').forEach(step => {
                const number = Number(step.dataset.excelStep);
                step.classList.toggle('is-complete', number < stage);
                step.classList.toggle('is-active', number === stage);
            });
        }

        function renderProgress(data) {
            showOnly(progress);
            setSteps(data.stage);
            root.querySelector('[data-excel-progress-title]').textContent = data.state === STATE.registering ? 'در حال وارد کردن اطلاعات' : 'در حال بررسی فایل';
            root.querySelector('[data-excel-progress-file]').textContent = data.fileName;
            root.querySelector('[data-excel-message]').textContent = data.message;
            root.querySelector('[data-excel-elapsed]').textContent = humanTime(data.elapsedSeconds || 0);
            const hasRealProgress = Number.isFinite(data.percent) && data.totalRows > 0;
            root.querySelector('[data-excel-count-text]').textContent = hasRealProgress ? `${data.processedRows} از ${data.totalRows} ردیف پردازش شد` : 'در حال آماده‌سازی اطلاعات واقعی…';
            root.querySelector('[data-excel-percent]').textContent = hasRealProgress ? `${data.percent}% تکمیل شده` : '';
            const bar = root.querySelector('[data-excel-bar]');
            bar.style.width = hasRealProgress ? `${Math.min(100, data.percent)}%` : '0';
            bar.parentElement.setAttribute('aria-valuenow', hasRealProgress ? String(data.percent) : '0');
            root.querySelector('[data-excel-cancel]').classList.toggle('d-none', !data.canCancel);
        }

        function renderValidation(data) {
            showOnly(validation);
            setSteps(3);
            root.querySelector('[data-excel-total]').textContent = data.totalRows;
            root.querySelector('[data-excel-valid]').textContent = data.validRows;
            root.querySelector('[data-excel-warning]').textContent = data.warningRows;
            root.querySelector('[data-excel-error]').textContent = data.errorRows;
            root.querySelector('[data-excel-duplicate]').textContent = data.duplicateRows;
            const conflict = root.querySelector('[data-excel-conflict]');
            if (conflict) conflict.textContent = data.conflictRows || 0;
            root.querySelector('[data-excel-file-name]').textContent = data.fileName;
            root.querySelector('[data-excel-sheet-info]').textContent = [data.sheetCount ? `${data.sheetCount} شیت` : '', data.selectedSheet ? `شیت: ${data.selectedSheet}` : ''].filter(Boolean).join(' · ');

            const rows = data.previewRows || [];
            const previewWrap = root.querySelector('[data-excel-preview-wrap]');
            previewWrap.classList.toggle('d-none', !rows.length);
            if (rows.length) {
                const keys = Object.keys(rows[0].values || {});
                root.querySelector('[data-excel-preview-head]').innerHTML = `<tr><th>ردیف</th>${keys.map(key => `<th>${escapeHtml(key)}</th>`).join('')}</tr>`;
                root.querySelector('[data-excel-preview-body]').innerHTML = rows.map(row => `<tr><td>${row.row}</td>${keys.map(key => `<td>${escapeHtml(row.values[key] || '—')}</td>`).join('')}</tr>`).join('');
            }

            const issues = data.issues || [];
            root.querySelector('[data-excel-issues-wrap]').classList.toggle('d-none', !issues.length);
            root.querySelector('[data-excel-issues]').innerHTML = issues.map(issue => `<li class="${issue.severity === 'warning' ? 'is-warning' : ''}">ردیف ${issue.row || '—'}، ستون ${escapeHtml(issue.column)}: ${escapeHtml(issue.message)}</li>`).join('');
            const errorReport = root.querySelector('[data-excel-error-report]');
            if (errorReport) {
                errorReport.classList.toggle('d-none', !issues.length || !root.dataset.errorReportUrl);
                if (root.dataset.errorReportUrl) errorReport.href = urlFor(root.dataset.errorReportUrl);
            }
            const confirm = root.querySelector('[data-excel-confirm]');
            if (confirm) {
                const allowValidWithErrors = root.dataset.allowValidWithErrors === 'true';
                confirm.disabled = data.validRows === 0 || (data.errorRows > 0 && !allowValidWithErrors);
                confirm.title = data.errorRows > 0 && !allowValidWithErrors ? 'ابتدا خطاهای فایل را اصلاح کنید.' : '';
            }
        }

        function renderResult(data, failed) {
            showOnly(result);
            const icon = root.querySelector('[data-excel-result-icon]');
            icon.classList.toggle('is-error', failed);
            icon.innerHTML = failed ? '<i class="bi bi-exclamation-lg"></i>' : '<i class="bi bi-check2"></i>';
            root.querySelector('[data-excel-result-title]').textContent = failed ? 'عملیات کامل نشد' : (root.dataset.successTitle || 'اطلاعات با موفقیت وارد شد');
            root.querySelector('[data-excel-result-message]').textContent = data.message;
            const created = root.querySelector('[data-excel-created]');
            const rejected = root.querySelector('[data-excel-rejected]');
            const added = root.querySelector('[data-excel-new]');
            const updated = root.querySelector('[data-excel-updated]');
            const duration = root.querySelector('[data-excel-duration]');
            if (created) created.textContent = data.createdRows || 0;
            if (rejected) rejected.textContent = data.rejectedRows || 0;
            if (added) added.textContent = data.createdRows || 0;
            if (updated) updated.textContent = data.updatedRows || 0;
            const resultDuplicate = root.querySelector('[data-excel-result-duplicate]');
            const resultConflict = root.querySelector('[data-excel-result-conflict]');
            const resultInvalid = root.querySelector('[data-excel-result-invalid]');
            if (resultDuplicate) resultDuplicate.textContent = data.duplicateRows || 0;
            if (resultConflict) resultConflict.textContent = data.conflictRows || 0;
            if (resultInvalid) resultInvalid.textContent = data.errorRows || 0;
            if (duration) duration.textContent = humanTime(data.elapsedSeconds || 0);
            const viewLink = root.querySelector('[data-excel-view]');
            if (viewLink) {
                viewLink.classList.toggle('d-none', failed);
                viewLink.href = data.redirectUrl || viewLink.getAttribute('href');
            }
            const importReport = root.querySelector('[data-excel-import-report]');
            if (importReport) {
                importReport.classList.toggle('d-none', failed || !root.dataset.importReportUrl);
                if (root.dataset.importReportUrl) importReport.href = urlFor(root.dataset.importReportUrl);
            }
            root.querySelector('[data-excel-retry]').classList.toggle('d-none', !failed);
            if (!failed) localStorage.removeItem(storageKey);
        }

        async function poll() {
            if (!jobId) return;
            try {
                const response = await fetch(urlFor(root.dataset.statusUrl), { credentials: 'same-origin', cache: 'no-store' });
                if (!response.ok) throw new Error('وضعیت Job دریافت نشد.');
                const data = await response.json();
                if (data.state === STATE.ready) return renderValidation(data);
                if (data.state === STATE.completed) return renderResult(data, false);
                if (data.state === STATE.failed || data.state === STATE.cancelled) return renderResult(data, true);
                renderProgress(data);
                pollTimer = window.setTimeout(poll, 900);
            } catch (error) {
                renderResult({ message: 'ارتباط با سرور قطع شد؛ پیشرفت Job از بین نرفته است. روی «تلاش دوباره» بزنید.', elapsedSeconds: 0 }, true);
            }
        }

        async function post(url, data) {
            const response = await fetch(url, { method: 'POST', body: data, credentials: 'same-origin' });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(payload.message || 'درخواست انجام نشد.');
            return payload;
        }

        const externalApi = {
            progress: data => renderProgress(Object.assign({ state: STATE.processing, stage: 2, fileName: fileInput.files[0]?.name || '', elapsedSeconds: 0 }, data || {})),
            ready: data => renderValidation(Object.assign({ totalRows: 0, validRows: 0, warningRows: 0, errorRows: 0, duplicateRows: 0, issues: [], previewRows: [], fileName: fileInput.files[0]?.name || '' }, data || {})),
            complete: data => renderResult(Object.assign({ message: 'فایل با موفقیت پردازش شد.', createdRows: 0, rejectedRows: 0, updatedRows: 0, elapsedSeconds: 0 }, data || {}), false),
            fail: message => renderResult({ message: message || 'عملیات کامل نشد.', elapsedSeconds: 0 }, true),
            reset
        };

        startButton.addEventListener('click', async () => {
            if (!fileInput.files.length || !form) return;
            startButton.disabled = true;
            showOnly(progress);
            renderProgress({ state: STATE.processing, stage: 1, fileName: fileInput.files[0].name, message: 'در حال آپلود فایل…', elapsedSeconds: 0 });
            if (mode === 'form') {
                window.requestAnimationFrame(() => HTMLFormElement.prototype.submit.call(form));
                return;
            }
            if (mode === 'external') {
                root.dispatchEvent(new CustomEvent('excel-import:start', { detail: Object.assign({ file: fileInput.files[0] }, externalApi) }));
                startButton.disabled = false;
                return;
            }
            try {
                const response = await post(root.dataset.startUrl, new FormData(form));
                jobId = response.jobId;
                localStorage.setItem(storageKey, jobId);
                poll();
            } catch (error) {
                renderResult({ message: error.message, elapsedSeconds: 0 }, true);
            } finally {
                startButton.disabled = false;
            }
        });

        root.querySelector('[data-excel-confirm]')?.addEventListener('click', async () => {
            if (mode === 'external') {
                renderProgress({ state: STATE.registering, stage: 4, fileName: fileInput.files[0]?.name || '', message: 'در حال ثبت اطلاعات…', elapsedSeconds: 0 });
                root.dispatchEvent(new CustomEvent('excel-import:confirm', { detail: Object.assign({ file: fileInput.files[0] }, externalApi) }));
                return;
            }
            try { await post(urlFor(root.dataset.confirmUrl), tokenData()); renderProgress({ state: STATE.registering, stage: 4, fileName: fileInput.files[0]?.name || '', message: 'در حال ثبت داخل Transaction…', elapsedSeconds: 0 }); poll(); }
            catch (error) { renderResult({ message: error.message, elapsedSeconds: 0 }, true); }
        });
        root.querySelector('[data-excel-cancel]').addEventListener('click', async () => { try { await post(urlFor(root.dataset.cancelUrl), tokenData()); poll(); } catch (error) { localError(error.message); } });
        root.querySelector('[data-excel-retry]').addEventListener('click', () => mode === 'job' ? poll() : reset());
        root.querySelector('[data-excel-another]').addEventListener('click', reset);
        root.querySelector('[data-excel-reupload]').addEventListener('click', reset);
        root.querySelector('[data-excel-remove]').addEventListener('click', reset);
        root.querySelector('[data-excel-change]').addEventListener('click', () => fileInput.click());
        root.addEventListener('excel-import:reset-request', reset);
        fileInput.addEventListener('change', () => selectFile(fileInput.files[0]));
        dropzone.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); fileInput.click(); } });
        ['dragenter', 'dragover'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.add('is-dragging'); }));
        ['dragleave', 'drop'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.remove('is-dragging'); }));
        dropzone.addEventListener('drop', event => selectFile(event.dataTransfer.files[0]));
        // کامپوننت همیشه داخل مودال مشترک است؛ با بستن مودال فایل انتخاب‌شده و پنل نتیجه
        // پاک می‌شود تا باز کردن بعدی از صفر شروع شود و فایل هم داخل فرم صفحه باقی نماند.
        // تنها استثنا Jobِ نیمه‌تمام سرور است که باید بعد از باز شدن دوباره ادامه پیدا کند.
        const modalHost = root.closest('.modal');
        if (modalHost) {
            modalHost.addEventListener('hidden.bs.modal', () => {
                if (mode === 'job' && jobId) return;
                reset();
            });
        }

        if (mode === 'job' && jobId) poll();
    }

    function escapeHtml(value) {
        return String(value ?? '').replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char]));
    }

    function boot(scope) {
        const host = scope && typeof scope.querySelectorAll === 'function' ? scope : document;
        host.querySelectorAll('[data-excel-import]').forEach(init);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => boot(document), { once: true });
    } else {
        boot(document);
    }

    // This file is a shared layout script, so it is never re-executed after an SPA
    // page swap. `ptg:page-ready` is the canonical post-swap signal (window-level,
    // same hook core.js/tables.js use); without it the uploader on a page opened
    // through in-app navigation stays uninitialised and its start button never enables.
    window.addEventListener('ptg:page-ready', () => boot(document));
    document.addEventListener('ptg:page-loaded', event => boot(event.target));
})();
