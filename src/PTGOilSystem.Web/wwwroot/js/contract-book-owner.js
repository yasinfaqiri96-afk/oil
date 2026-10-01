// «مالک دفتر» در جدول شرکای فرم قرارداد (ایجاد و ویرایش).
//
// مالک دفتر = شریکی که در اطلاعات شرکت به‌عنوان مالک دفترِ شرکتِ انتخاب‌شده ثبت شده است.
// این اسکریپت فقط نمایش است: کنار همان شریک یک برچسب کوچک می‌گذارد، و اگر شرکت مالک دفتر
// ندارد یا مالک دفترش در شرکا نیست، یک هشدار کوتاه نشان می‌دهد. چیزی را مسدود نمی‌کند.
(function () {
    'use strict';

    var MissingOwnerMessage = 'مالک دفتر این قرارداد مشخص نیست؛ تا آن را در اطلاعات شرکت تعیین نکنید، پرداخت از صندوق شرکت برای این قرارداد ثبت نمی‌شود.';
    var OwnerNotPartnerMessage = 'مالک دفتر این شرکت در شرکای این قرارداد نیست؛ پرداخت از صندوق شرکت برای این قرارداد ثبت نمی‌شود.';

    function init() {
        var section = document.getElementById('partnerSection');
        var rowsBody = document.getElementById('partnerRowsBody');
        var companySelect = document.getElementById('CompanyId');
        var ownershipType = document.getElementById('ownershipType');
        var note = section ? section.querySelector('[data-book-owner-note]') : null;
        if (!section || !rowsBody || !companySelect || !ownershipType) {
            return;
        }

        var owners = {};
        try {
            owners = JSON.parse(section.getAttribute('data-book-owners') || '{}');
        } catch (e) {
            owners = {};
        }

        function refresh() {
            var ownerId = owners[companySelect.value];
            var ownerKey = ownerId === undefined || ownerId === null ? '' : String(ownerId);
            var ownerListed = false;

            rowsBody.querySelectorAll('[data-partner-row]').forEach(function (row) {
                var select = row.querySelector('select');
                var badge = row.querySelector('[data-book-owner-badge]');
                var isOwner = ownerKey !== '' && !!select && select.value === ownerKey;
                if (badge) {
                    badge.classList.toggle('d-none', !isOwner);
                }
                if (isOwner) {
                    ownerListed = true;
                }
            });

            if (!note) {
                return;
            }

            var message = '';
            if (ownershipType.value === '2' && companySelect.value) {
                if (ownerKey === '') {
                    message = MissingOwnerMessage;
                } else if (!ownerListed) {
                    message = OwnerNotPartnerMessage;
                }
            }

            note.textContent = message;
            note.classList.toggle('d-none', message === '');
        }

        companySelect.addEventListener('change', refresh);
        ownershipType.addEventListener('change', refresh);
        rowsBody.addEventListener('change', refresh);
        // افزودن/حذفِ ردیف شریک از اسکریپتِ خودِ صفحه انجام می‌شود؛ همین‌جا فقط تغییر ردیف‌ها شنیده می‌شود.
        new MutationObserver(refresh).observe(rowsBody, { childList: true });
        refresh();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
