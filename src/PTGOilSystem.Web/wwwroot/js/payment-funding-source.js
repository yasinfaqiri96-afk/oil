// «پرداخت توسط: شرکت / شریک» در فرم روزنامچه.
//
// حالت شرکت  : حساب نقد/بانک دیده و ارسال می‌شود؛ فیلد شریک خالی و disabled است.
// حالت شریک  : حساب نقد/بانک مخفی و disabled می‌شود تا اصلاً post نشود (صندوق شرکت تکان نمی‌خورد)
//              و فقط شرکای همان قرارداد در فهرست می‌آیند.
(function () {
    'use strict';

    function init() {
        var companyRadio = document.getElementById('fundingSourceCompany');
        var partnerRadio = document.getElementById('fundingSourcePartner');
        var partnerField = document.getElementById('paidByPartnerField');
        var partnerSelect = document.getElementById('paidByPartnerSelect');
        var cashField = document.getElementById('cashAccountField');
        var cashSelect = document.getElementById('cashAccountSelect');
        var contractSelect = document.getElementById('contractSelect');

        if (!companyRadio || !partnerRadio || !partnerField || !partnerSelect || !cashField) {
            return;
        }

        function apply() {
            var isPartner = partnerRadio.checked;

            partnerField.classList.toggle('d-none', !isPartner);
            partnerSelect.disabled = !isPartner;
            if (!isPartner) {
                partnerSelect.value = '';
            }

            cashField.classList.toggle('d-none', isPartner);
            if (cashSelect) {
                cashSelect.disabled = isPartner;
                if (isPartner) {
                    cashSelect.value = '';
                }
            }
        }

        function reloadPartners() {
            if (!contractSelect) {
                return;
            }

            var contractId = contractSelect.value;
            var previous = partnerSelect.value;
            if (!contractId) {
                partnerSelect.innerHTML = '<option value="">انتخاب شریک</option>';
                return;
            }

            fetch('/Payments/ContractPartnerOptions?contractId=' + encodeURIComponent(contractId), {
                headers: { 'Accept': 'application/json' }
            })
                .then(function (response) { return response.ok ? response.json() : { partners: [] }; })
                .then(function (data) {
                    var options = ['<option value="">انتخاب شریک</option>'];
                    (data.partners || []).forEach(function (partner) {
                        var selected = String(partner.id) === previous ? ' selected' : '';
                        options.push(
                            '<option value="' + partner.id + '"' + selected + '>' +
                            partner.name + ' (' + Number(partner.sharePercent).toFixed(2) + '%)' +
                            '</option>');
                    });
                    partnerSelect.innerHTML = options.join('');
                })
                .catch(function () { /* فهرست دست‌نخورده می‌ماند؛ اعتبارسنجی سمت سرور تصمیم نهایی است. */ });
        }

        companyRadio.addEventListener('change', apply);
        partnerRadio.addEventListener('change', apply);
        if (contractSelect) {
            contractSelect.addEventListener('change', reloadPartners);
        }

        apply();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
