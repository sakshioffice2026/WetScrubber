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
        optimizeUrl: root.dataset.optimizeUrl,
        saveUrl: root.dataset.saveUrl
    };

    var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    var token = tokenInput ? tokenInput.value : '';

    var el = {
        log: document.getElementById('chatLog'),
        input: document.getElementById('chatInput'),
        send: document.getElementById('btnSend'),
        optimize: document.getElementById('btnOptimize'),
        reset: document.getElementById('btnReset'),
        draft: document.getElementById('draftCard'),
        results: document.getElementById('resultCard'),
        checks: document.getElementById('checksCard'),
        confirm: document.getElementById('btnConfirm'),
        comparison: document.getElementById('optimizationComparisonCard'),
        comparisonBody: document.getElementById('optimizationComparisonBody'),
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

    var MATERIAL_NAMES = {
        1: 'FRP',
        2: 'PP',
        3: 'HDPE',
        4: 'PVC',
        5: 'SS316',
        6: 'HastelloyC',
        7: 'CarbonSteel'
    };

    var busy = false;
    var designComplete = false;

    function fmt(value) {
        if (typeof value === 'number' && isFinite(value)) {
            return value.toLocaleString(undefined, {
                maximumFractionDigits: 2
            });
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
        el.optimize.disabled = state;
        el.input.disabled = state;
        el.log.setAttribute('aria-busy', state ? 'true' : 'false');
    }

    // ── Typing / pending / error states ─────────────────────────

    function stopTimers(node) {
        if (node && node._slowTimer) {
            clearTimeout(node._slowTimer);
            node._slowTimer = null;
        }
    }

    // Shows an animated "typing" indicator inside an AI message bubble.
    function showTyping(node, label) {
        stopTimers(node);
        clear(node);

        node.classList.remove('msg-error');
        node.classList.add('msg-pending');

        var dots = document.createElement('span');
        dots.className = 'typing-dots';
        dots.setAttribute('aria-hidden', 'true');

        for (var i = 0; i < 3; i++) {
            dots.appendChild(document.createElement('span'));
        }

        var text = document.createElement('span');
        text.className = 'typing-label';
        text.textContent = label;

        node.appendChild(dots);
        node.appendChild(text);

        // Reassure the user if the model takes a while.
        node._slowTimer = setTimeout(function () {
            text.textContent = label + ' — still working…';
        }, 8000);

        el.log.scrollTop = el.log.scrollHeight;
    }

    function finishPending(node) {
        stopTimers(node);
        node.classList.remove('msg-pending');
        node.classList.remove('msg-error');
    }

    function friendlyError(err) {
        if (err && err.status === 401) {
            return 'Your session has expired. Please sign in again.';
        }

        if (err && err.status === 429) {
            return 'Too many requests. Please wait a moment and try again.';
        }

        if (err && err.status >= 500) {
            return 'The assistant ran into a problem on the server. Please try again.';
        }

        if (err && err.status) {
            return err.message || 'The request could not be completed.';
        }

        return 'Could not reach the server. Check your connection and try again.';
    }

    // Replaces the bubble content with an error and an optional Retry button.
    function showError(node, err, retryFn) {
        stopTimers(node);
        clear(node);

        node.classList.remove('msg-pending');
        node.classList.add('msg-error');
        node.setAttribute('role', 'alert');

        var text = document.createElement('div');
        text.className = 'msg-error-text';
        text.textContent = friendlyError(err);
        node.appendChild(text);

        var canRetry = retryFn && !(err && (err.status === 401 || err.status === 403));

        if (canRetry) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'msg-retry';
            btn.textContent = 'Retry';

            btn.addEventListener('click', function () {
                if (busy) {
                    return;
                }

                node.removeAttribute('role');
                retryFn(node);
            });

            node.appendChild(btn);
        }

        el.log.scrollTop = el.log.scrollHeight;
    }

    function afterRequest() {
        setBusy(false);
        el.input.focus();
        el.log.scrollTop = el.log.scrollHeight;
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
            return res.json().catch(function () {
                return {};
            }).then(function (data) {
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

                if ((key === 'shellMaterial' ||
                    key === 'internalMaterial') &&
                    typeof value === 'number') {
                    value = MATERIAL_NAMES[value] || value;
                }

                rows.push({
                    label: DRAFT_LABELS[key][0],
                    value: fmt(value),
                    unit: DRAFT_LABELS[key][1]
                });
            });
        }

        renderRows(
            el.draft,
            rows,
            'Nothing captured yet.'
        );
    }

    function renderResults(calc) {
        if (!calc) {
            renderRows(
                el.results,
                [],
                'Results appear once all required inputs are provided.'
            );
            return;
        }

        if (calc.error) {
            renderRows(
                el.results,
                [{
                    label: 'Error',
                    value: calc.error,
                    unit: ''
                }],
                ''
            );
            return;
        }

        var rows = [];

        Object.keys(RESULT_LABELS).forEach(function (key) {
            if (calc[key] === null || calc[key] === undefined) {
                return;
            }

            rows.push({
                label: RESULT_LABELS[key][0],
                value: fmt(calc[key]),
                unit: RESULT_LABELS[key][1]
            });
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
            row.className =
                'check-row check-' + status.toLowerCase();

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

    function getValue(source, keys) {
        if (!source) {
            return null;
        }

        for (var i = 0; i < keys.length; i++) {
            if (source[keys[i]] !== null &&
                source[keys[i]] !== undefined) {
                return source[keys[i]];
            }
        }

        return null;
    }

    function renderComparison(data) {
        var before = data.before || data.current || data.original;
        var after = data.after || data.optimized;

        if (!before || !after) {
            el.comparison.hidden = true;
            return;
        }

        clear(el.comparisonBody);

        var rows = [
            {
                label: 'Packing',
                before: getValue(before, ['packingCode', 'packingUsed']),
                after: getValue(after, ['packingCode', 'packingUsed'])
            },
            {
                label: 'L/G (L/m³)',
                before: getValue(before, ['actualLGRatio', 'liquidToGasRatio']),
                after: getValue(after, ['actualLGRatio', 'liquidToGasRatio'])
            },
            {
                label: 'Total Power (kW)',
                before: getValue(before, ['totalPowerKW']),
                after: getValue(after, ['totalPowerKW'])
            },
            {
                label: 'Tower Diameter (m)',
                before: getValue(before, ['towerDiameterM']),
                after: getValue(after, ['towerDiameterM'])
            },
            {
                label: 'Tower Height (m)',
                before: getValue(before, ['towerHeightM']),
                after: getValue(after, ['towerHeightM'])
            },
            {
                label: 'Liquid Loading (m³/m²·h)',
                before: getValue(before, ['liquidLoadingM3M2Hr']),
                after: getValue(after, ['liquidLoadingM3M2Hr'])
            },
            {
                label: 'Pressure Drop (Pa)',
                before: getValue(before, ['pressureDropPa']),
                after: getValue(after, ['pressureDropPa'])
            },
            {
                label: 'Flooding (%)',
                before: getValue(before, ['percentFlood']),
                after: getValue(after, ['percentFlood'])
            },
            {
                label: 'Predicted Removal (%)',
                before: getValue(before, ['removalEfficiencyPct']),
                after: getValue(after, ['removalEfficiencyPct'])
            }
        ];

        rows.forEach(function (item) {
            if (item.before === null && item.after === null) {
                return;
            }

            var tr = document.createElement('tr');

            var parameter = document.createElement('td');
            parameter.textContent = item.label;

            var beforeCell = document.createElement('td');
            beforeCell.textContent =
                item.before === null ? '—' : fmt(item.before);

            var afterCell = document.createElement('td');
            afterCell.textContent =
                item.after === null ? '—' : fmt(item.after);

            tr.appendChild(parameter);
            tr.appendChild(beforeCell);
            tr.appendChild(afterCell);

            el.comparisonBody.appendChild(tr);
        });

        el.comparison.hidden =
            el.comparisonBody.children.length === 0;
    }

    function updateConfirm(data) {
        var hasError =
            data.calculation &&
            data.calculation.error;

        designComplete =
            !!data.designComplete &&
            !hasError;

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

        requestChat(text, addMessage('', 'msg-ai'));
    }

    function requestChat(text, pending) {
        setBusy(true);
        showTyping(pending, 'Thinking');

        postJson(cfg.chatUrl, {
            message: text
        })
            .then(function (data) {
                finishPending(pending);
                pending.textContent = data.message || '';

                renderDraft(data.draft);
                renderResults(data.calculation);
                renderChecks(data.checks);
                updateConfirm(data);
            })
            .catch(function (err) {
                showError(pending, err, function (node) {
                    requestChat(text, node);
                });
            })
            .then(afterRequest);
    }

    function optimizeDesign() {
        if (busy || !cfg.optimizeUrl) {
            return;
        }

        requestOptimize(addMessage('', 'msg-ai'));
    }

    function requestOptimize(pending) {
        setBusy(true);
        showTyping(pending, 'Optimizing the design');

        postJson(cfg.optimizeUrl, {
            projectId: cfg.projectId
        })
            .then(function (data) {
                finishPending(pending);
                pending.textContent =
                    data.message || 'Optimization completed.';

                renderComparison(data);

                if (data.calculation) {
                    renderResults(data.calculation);
                }

                if (data.checks) {
                    renderChecks(data.checks);
                }

                updateConfirm(data);
            })
            .catch(function (err) {
                showError(pending, err, requestOptimize);
            })
            .then(afterRequest);
    }

    function resetDesign() {
        if (busy) {
            return;
        }

        setBusy(true);
        el.reset.disabled = true;

        postJson(cfg.resetUrl, {})
            .then(function () {
                clear(el.log);

                addMessage(
                    'Starting a new design. Describe your requirement.',
                    'msg-ai'
                );

                renderDraft(null);
                renderResults(null);
                renderChecks(null);

                clear(el.comparisonBody);
                el.comparison.hidden = true;

                designComplete = false;
                el.confirm.disabled = true;
            })
            .catch(function (err) {
                var node = addMessage('', 'msg-ai');
                showError(node, err, function (retryNode) {
                    if (retryNode.parentNode) {
                        retryNode.parentNode.removeChild(retryNode);
                    }

                    resetDesign();
                });
            })
            .then(function () {
                el.reset.disabled = false;
                afterRequest();
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
        var originalLabel = el.modalSave.textContent;

        el.modalSave.disabled = true;
        el.modalSave.textContent = 'Saving…';
        el.modalError.hidden = true;

        postJson(cfg.saveUrl, {
            projectId: cfg.projectId,
            designName: el.modalName.value.trim()
        })
            .then(function (data) {
                if (data.redirectUrl) {
                    window.location.href =
                        data.redirectUrl;
                } else {
                    closeModal();
                }
            })
            .catch(function (err) {
                el.modalError.textContent = friendlyError(err);

                el.modalError.hidden = false;
            })
            .then(function () {
                el.modalSave.disabled = false;
                el.modalSave.textContent = originalLabel;
            });
    }

    el.send.addEventListener('click', sendMessage);

    el.optimize.addEventListener(
        'click',
        optimizeDesign
    );

    el.input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });

    el.reset.addEventListener(
        'click',
        resetDesign
    );

    el.confirm.addEventListener(
        'click',
        openModal
    );

    el.modalCancel.addEventListener(
        'click',
        closeModal
    );

    el.modalSave.addEventListener(
        'click',
        saveDesign
    );

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