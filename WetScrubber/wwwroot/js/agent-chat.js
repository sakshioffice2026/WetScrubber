(function () {
    'use strict';

    var root = document.getElementById('agentRoot');
    if (!root) {
        return;
    }

    var cfg = {
        projectId: parseInt(root.dataset.projectId, 10),
        chatUrl: root.dataset.chatUrl,
        resetUrl: root.dataset.resetUrl,
        saveUrl: root.dataset.saveUrl
    };

    var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    var token = tokenInput ? tokenInput.value : '';

    var el = {
        log: document.getElementById('chatLog'),
        input: document.getElementById('chatInput'),
        send: document.getElementById('btnSend'),
        reset: document.getElementById('btnReset'),
        draft: document.getElementById('draftCard'),
        results: document.getElementById('resultCard'),
        checks: document.getElementById('checksCard'),
        confirm: document.getElementById('btnConfirm'),
        modal: document.getElementById('saveModal'),
        modalName: document.getElementById('saveDesignName'),
        modalError: document.getElementById('saveModalError'),
        modalCancel: document.getElementById('btnSaveCancel'),
        modalSave: document.getElementById('btnSaveConfirm')
    };

    var DRAFT_LABELS = {
        normalFlowRate: ['Gas flow (Nm³/hr)', ''],
        actualFlowRate: ['Gas flow (m³/hr)', ''],
        inletTemperature: ['Inlet temperature', '°C'],
        inletPressure: ['Inlet pressure', 'Pa'],
        moistureContent: ['Moisture', '% vol'],
        pollutantName: ['Pollutant', ''],
        inletConcentration: ['Inlet concentration', 'mg/Nm³'],
        targetRemovalEfficiency: ['Target removal', '%'],
        liquidName: ['Scrubbing liquid', ''],
        liquidConcentration: ['Liquid concentration', '% wt'],
        liquidPH: ['Liquid pH', ''],
        liquidTemperature: ['Liquid temperature', '°C'],
        liquidToGasRatio: ['L/G ratio', 'L/m³'],
        packingCode: ['Packing', ''],
        shellMaterial: ['Shell material', ''],
        internalMaterial: ['Packing / internals material', '']
    };

    var RESULT_LABELS = {
        towerDiameterM: ['Tower diameter', 'm'],
        towerHeightM: ['Tower height', 'm'],
        packingHeightM: ['Packing height', 'm'],
        gasVelocityMs: ['Gas velocity', 'm/s'],
        pressureDropPa: ['Pressure drop', 'Pa'],
        percentFlood: ['Flooding', '%'],
        removalEfficiencyPct: ['Predicted removal', '%'],
        absorptionFactor: ['Absorption factor', ''],
        minLGRatio: ['Minimum L/G', ''],
        actualLGRatio: ['Actual L/G', ''],
        liquidFlowRateM3Hr: ['Liquid flow', 'm³/hr'],
        totalPowerKW: ['Total power', 'kW']
    };

    var MATERIAL_NAMES = { 1: 'FRP', 2: 'PP', 3: 'HDPE', 4: 'PVC', 5: 'SS316', 6: 'HastelloyC', 7: 'CarbonSteel' };

    var busy = false;
    var designComplete = false;

    function fmt(value) {
        if (typeof value === 'number' && isFinite(value)) {
            return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
        }
        return String(value);
    }

    function clear(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }

    function addMessage(text, kind) {
        var div = document.createElement('div');
        div.className = 'msg ' + kind;
        div.textContent = text;
        el.log.appendChild(div);
        el.log.scrollTop = el.log.scrollHeight;
        return div;
    }

    function setBusy(state) {
        busy = state;
        el.send.disabled = state;
        el.input.disabled = state;
    }

    function postJson(url, body) {
        return fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            credentials: 'same-origin',
            body: JSON.stringify(body || {})
        }).then(function (res) {
            return res.json().catch(function () { return {}; }).then(function (data) {
                if (!res.ok) {
                    var err = new Error(data.error || 'Request failed.');
                    err.status = res.status;
                    throw err;
                }
                return data;
            });
        });
    }

    function renderRows(container, rows, emptyText) {
        clear(container);

        if (!rows.length) {
            var empty = document.createElement('div');
            empty.className = 'pcard-empty';
            empty.textContent = emptyText;
            container.appendChild(empty);
            return;
        }

        var grid = document.createElement('div');
        grid.className = 'kv-grid';

        rows.forEach(function (r) {
            var k = document.createElement('div');
            k.className = 'kv-key';
            k.textContent = r.label;

            var v = document.createElement('div');
            v.className = 'kv-val';
            v.textContent = r.value + (r.unit ? ' ' + r.unit : '');

            grid.appendChild(k);
            grid.appendChild(v);
        });

        container.appendChild(grid);
    }

    function renderDraft(draft) {
        var rows = [];

        if (draft) {
            Object.keys(DRAFT_LABELS).forEach(function (key) {
                var value = draft[key];
                if (value === null || value === undefined || value === '') {
                    return;
                }
                if ((key === 'shellMaterial' || key === 'internalMaterial') && typeof value === 'number') {
                    value = MATERIAL_NAMES[value] || value;
                }
                rows.push({ label: DRAFT_LABELS[key][0], value: fmt(value), unit: DRAFT_LABELS[key][1] });
            });
        }

        renderRows(el.draft, rows, 'Nothing captured yet.');
    }

    function renderResults(calc) {
        if (!calc) {
            renderRows(el.results, [], 'Results appear once all required inputs are provided.');
            return;
        }

        if (calc.error) {
            renderRows(el.results, [{ label: 'Error', value: calc.error, unit: '' }], '');
            return;
        }

        var rows = [];
        Object.keys(RESULT_LABELS).forEach(function (key) {
            if (calc[key] === null || calc[key] === undefined) {
                return;
            }
            rows.push({ label: RESULT_LABELS[key][0], value: fmt(calc[key]), unit: RESULT_LABELS[key][1] });
        });

        renderRows(el.results, rows, 'No results.');

        if (calc.notes && calc.notes.length) {
            calc.notes.forEach(function (n) {
                var note = document.createElement('div');
                note.className = 'pcard-note';
                note.textContent = n;
                el.results.appendChild(note);
            });
        }
    }

    function renderChecks(checks) {
        clear(el.checks);

        if (!checks || !checks.length) {
            var empty = document.createElement('div');
            empty.className = 'pcard-empty';
            empty.textContent = 'No checks run yet.';
            el.checks.appendChild(empty);
            return;
        }

        checks.forEach(function (c) {
            var status = String(c.status || '').toUpperCase();

            var row = document.createElement('div');
            row.className = 'check-row check-' + status.toLowerCase();

            var badge = document.createElement('span');
            badge.className = 'check-badge';
            badge.textContent = status;

            var body = document.createElement('div');
            body.className = 'check-body';

            var name = document.createElement('div');
            name.className = 'check-name';
            name.textContent = c.name;

            var detail = document.createElement('div');
            detail.className = 'check-detail';
            detail.textContent = c.detail;

            body.appendChild(name);
            body.appendChild(detail);

            if (status !== 'PASS' && c.parameter) {
                var param = document.createElement('div');
                param.className = 'check-param';
                param.textContent = 'Adjust: ' + c.parameter;
                body.appendChild(param);
            }

            row.appendChild(badge);
            row.appendChild(body);
            el.checks.appendChild(row);
        });
    }

    function updateConfirm(data) {
        var hasError = data.calculation && data.calculation.error;
        designComplete = !!data.designComplete && !hasError;
        el.confirm.disabled = !designComplete;
    }

    function sendMessage() {
        if (busy) {
            return;
        }

        var text = el.input.value.trim();
        if (!text) {
            return;
        }

        addMessage(text, 'msg-user');
        el.input.value = '';
        setBusy(true);

        var pending = addMessage('Thinking…', 'msg-ai msg-pending');

        postJson(cfg.chatUrl, { message: text })
            .then(function (data) {
                pending.classList.remove('msg-pending');
                pending.textContent = data.message || '';
                renderDraft(data.draft);
                renderResults(data.calculation);
                renderChecks(data.checks);
                updateConfirm(data);
            })
            .catch(function (err) {
                pending.classList.remove('msg-pending');
                pending.classList.add('msg-error');
                pending.textContent = err.status === 401
                    ? 'Your session has expired. Please log in again.'
                    : err.message;
            })
            .then(function () {
                setBusy(false);
                el.input.focus();
                el.log.scrollTop = el.log.scrollHeight;
            });
    }

    function resetDesign() {
        if (busy) {
            return;
        }

        postJson(cfg.resetUrl, {})
            .then(function () {
                clear(el.log);
                addMessage('Starting a new design. Describe your requirement.', 'msg-ai');
                renderDraft(null);
                renderResults(null);
                renderChecks(null);
                designComplete = false;
                el.confirm.disabled = true;
            })
            .catch(function (err) {
                addMessage(err.message, 'msg-ai msg-error');
            });
    }

    function openModal() {
        if (!designComplete) {
            return;
        }
        el.modalError.hidden = true;
        el.modalName.value = '';
        el.modal.hidden = false;
        el.modalName.focus();
    }

    function closeModal() {
        el.modal.hidden = true;
    }

    function saveDesign() {
        el.modalSave.disabled = true;
        el.modalError.hidden = true;

        postJson(cfg.saveUrl, {
            projectId: cfg.projectId,
            designName: el.modalName.value.trim()
        })
            .then(function (data) {
                if (data.redirectUrl) {
                    window.location.href = data.redirectUrl;
                } else {
                    closeModal();
                }
            })
            .catch(function (err) {
                el.modalError.textContent = err.message;
                el.modalError.hidden = false;
            })
            .then(function () {
                el.modalSave.disabled = false;
            });
    }

    el.send.addEventListener('click', sendMessage);

    el.input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });

    el.reset.addEventListener('click', resetDesign);
    el.confirm.addEventListener('click', openModal);
    el.modalCancel.addEventListener('click', closeModal);
    el.modalSave.addEventListener('click', saveDesign);

    el.modal.addEventListener('click', function (e) {
        if (e.target === el.modal) {
            closeModal();
        }
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !el.modal.hidden) {
            closeModal();
        }
    });
})();
