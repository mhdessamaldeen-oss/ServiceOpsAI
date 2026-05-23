(function () {
    function ready(fn) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', fn);
            return;
        }

        fn();
    }

    ready(function initializeCopilotAssessmentLab() {
        const app = document.getElementById('copilot-assessment-app');
        if (!app) {
            return;
        }

        const runButton = document.getElementById('btnRunAll');
        const statusRate = document.getElementById('statSuccessRate');
        const statusLatency = document.getElementById('statLatency') || document.getElementById('statAvgLatency');
        const summary = document.getElementById('assessmentSummary');

        if (!runButton) {
            return;
        }

        const dataset = app.dataset;
        const runUrl = dataset.runUrl || '';
        const runLabel = dataset.runLabel || 'Run Assessment';
        const runningLabel = dataset.runningLabel || 'Running assessment...';
        const passLabel = dataset.passLabel || 'Pass';
        const failLabel = dataset.failLabel || 'Fail';
        const pendingLabel = dataset.pendingLabel || 'Pending';
        const completeTitle = dataset.assessmentCompleteTitle || 'Assessment Complete';
        const errorTitle = dataset.assessmentErrorTitle || 'Assessment Failed';

        let progressInterval;
        const progressContainer = document.getElementById('assessmentProgressContainer');
        const progressBar = document.getElementById('assessmentProgressBar');
        const progressStatus = document.getElementById('progressStatus');
        const progressCount = document.getElementById('progressCount');

        const runSelectedButton = document.getElementById('btnRunSelected');
        const stopButton = document.getElementById('btnStopAssessment');
        const catalogSearch = document.getElementById('catalogSearch');
        const sessionSelector = document.getElementById('sessionSelector');

        let runAbortController = null;
        let activeRunCaseIds = null;
        let hubConnection = null;

        // --- Global Run Stats ---
        let globalRunStats = {
            completedCount: 0,
            successCount: 0,
            totalLatency: 0,
            totalCases: 0
        };

        // --- SignalR Setup ---
        if (window.signalR) {
            hubConnection = new signalR.HubConnectionBuilder()
                .withUrl("/hubs/copilotAssessment")
                .withAutomaticReconnect()
                .build();

            hubConnection.on("ProgressUpdate", function (completedCount, totalCount, runId) {
                globalRunStats.completedCount = completedCount;
                globalRunStats.totalCases = totalCount;
                updateProgressUI(completedCount, totalCount, null, activeRunCaseIds);
            });

            hubConnection.on("PhaseUpdate", function (status) {
                updateLiveStatus(status);
            });

            hubConnection.on("CaseCompleted", function (result) {
                if (result) {
                    // Update global counters incrementally
                    globalRunStats.completedCount++;
                    if (result.isSuccess) globalRunStats.successCount++;
                    if (result.latencyMs) globalRunStats.totalLatency += result.latencyMs;

                    updateRows([result], { updateStats: true, isIncremental: true });
                }
            });

            hubConnection.start()

                .then(function() {
                    var sessionId = (sessionSelector && sessionSelector.value ? sessionSelector.value : null);
                    if (sessionId) {
                        hubConnection.invoke("JoinSession", sessionId.toString())
                            .catch(err => console.error("Error auto-joining SignalR group:", err));
                    }
                })
                .catch(err => console.error("SignalR Connection Error: ", err));
        }



        if (stopButton) {
            stopButton.addEventListener('click', function () {
                if (runAbortController) {
                    runAbortController.abort();
                    showDialog('info', 'Assessment Stopped', 'The assessment run has been cancelled.');
                    setRunningState(false);
                    stopProgressTracking();
                    activeRunCaseIds = null;
                }
            });
        }

        // ── Session & Filter Selection ────────────────────────────
        if (sessionSelector) {
            sessionSelector.addEventListener('change', function () {
                var url = new URL(window.location.href);
                if (this.value) {
                    url.searchParams.set('sessionId', this.value);
                } else {
                    url.searchParams.delete('sessionId');
                }
                window.location.href = url.toString();
            });
        }

        const statusFilterSelector = document.getElementById('statusFilterSelector');
        if (statusFilterSelector) {
            statusFilterSelector.addEventListener('change', function () {
                var url = new URL(window.location.href);
                if (this.value) {
                    url.searchParams.set('request.Filter', this.value);
                } else {
                    url.searchParams.delete('request.Filter');
                }
                // Reset to page 1 when filter changes
                url.searchParams.delete('request.PageIndex');
                window.location.href = url.toString();
            });
        }

        // ── Select All ────────────────────────────────────────────
        document.addEventListener('change', function (e) {
            if (e.target && e.target.id === 'selectAllCases') {
                var isChecked = e.target.checked;
                var selectors = getVisibleCaseSelectors(false);
                for (var i = 0; i < selectors.length; i++) {
                    selectors[i].checked = isChecked;
                }
                updateRunSelectedVisibility();
            }
        });

        document.addEventListener('change', function (e) {
            if (e.target && e.target.classList && e.target.classList.contains('case-selector')) {
                updateRunSelectedVisibility();
            }
        });

        // ── Catalog Search ────────────────────────────────────────
        if (catalogSearch) {
            catalogSearch.addEventListener('input', function () {
                var term = this.value.toLowerCase().trim();
                var rows = document.querySelectorAll('.assessment-row');
                for (var i = 0; i < rows.length; i++) {
                    var row = rows[i];
                    var question = (row.querySelector('.fw-semibold') || {}).textContent || '';
                    var category = row.dataset.category || '';
                    var diff = (row.querySelector('.difficulty-col .badge') || {}).textContent || '';

                    question = question.toLowerCase();
                    category = category.toLowerCase();
                    diff = diff.toLowerCase();

                    if (question.indexOf(term) !== -1 || category.indexOf(term) !== -1 || diff.indexOf(term) !== -1) {
                        row.classList.remove('d-none');
                    } else {
                        row.classList.add('d-none');
                    }
                }

                // Handle group headers visibility
                var headers = document.querySelectorAll('.table-group-header');
                for (var h = 0; h < headers.length; h++) {
                    var header = headers[h];
                    var hasVisible = false;
                    var current = header.nextElementSibling;
                    while (current && !current.classList.contains('table-group-header')) {
                        if (!current.classList.contains('d-none')) {
                            hasVisible = true;
                            break;
                        }
                        current = current.nextElementSibling;
                    }
                    header.classList.toggle('d-none', !hasVisible);
                }
                // Update selection UI based on new visibility
                updateRunSelectedVisibility();
            });
        }

        // ── Filter Buttons (Has Answer / Needs Answer) ─────────────
        var filterAllBtn = document.getElementById('filterAll');
        var filterNeedsAnswerBtn = document.getElementById('filterNeedsAnswer');
        var filterHasAnswerBtn = document.getElementById('filterHasAnswer');
        
        function applyFilter(filterType) {
            var rows = document.querySelectorAll('.assessment-row');
            for (var i = 0; i < rows.length; i++) {
                var row = rows[i];
                if (filterType === 'all') {
                    row.classList.remove('d-none');
                } else if (filterType === 'needs-answer') {
                    if (row.dataset.hasAnswer !== 'true') {
                        row.classList.remove('d-none');
                    } else {
                        row.classList.add('d-none');
                    }
                } else if (filterType === 'has-answer') {
                    if (row.dataset.hasAnswer === 'true') {
                        row.classList.remove('d-none');
                    } else {
                        row.classList.add('d-none');
                    }
                }
            }
            
            // Handle group headers visibility
            var headers = document.querySelectorAll('.table-group-header');
            for (var h = 0; h < headers.length; h++) {
                var header = headers[h];
                var hasVisible = false;
                var current = header.nextElementSibling;
                while (current && !current.classList.contains('table-group-header')) {
                    if (!current.classList.contains('d-none')) {
                        hasVisible = true;
                        break;
                    }
                    current = current.nextElementSibling;
                }
                header.classList.toggle('d-none', !hasVisible);
            }
            
            // Update button states
            if (filterAllBtn) filterAllBtn.classList.toggle('active', filterType === 'all');
            if (filterNeedsAnswerBtn) filterNeedsAnswerBtn.classList.toggle('active', filterType === 'needs-answer');
            if (filterHasAnswerBtn) filterHasAnswerBtn.classList.toggle('active', filterType === 'has-answer');
            
            // Update visible count and selection UI
            updateVisibleCount();
            updateRunSelectedVisibility();
        }
        
        function updateSummaryCounts() {
            var allRows = document.querySelectorAll('.assessment-row');
            var hasAnswer = 0;
            var needsAnswer = 0;
            
            for (var i = 0; i < allRows.length; i++) {
                if (allRows[i].dataset.hasAnswer === 'true') {
                    hasAnswer++;
                } else {
                    needsAnswer++;
                }
            }
            
            var countHasAnswer = document.getElementById('countHasAnswer');
            var countNeedsAnswer = document.getElementById('countNeedsAnswer');
            var countTotal = document.getElementById('countTotal');
            
            if (countHasAnswer) countHasAnswer.textContent = hasAnswer;
            if (countNeedsAnswer) countNeedsAnswer.textContent = needsAnswer;
            if (countTotal) countTotal.textContent = allRows.length;
        }
        
        if (filterAllBtn) {
            filterAllBtn.addEventListener('click', function() { applyFilter('all'); });
        }
        if (filterNeedsAnswerBtn) {
            filterNeedsAnswerBtn.addEventListener('click', function() { applyFilter('needs-answer'); });
        }
        if (filterHasAnswerBtn) {
            filterHasAnswerBtn.addEventListener('click', function() { applyFilter('has-answer'); });
        }
        
        // ── Update Visible Count ──────────────────────────────────
        function updateVisibleCount() {
            var visibleCount = document.getElementById('visibleCount');
            if (visibleCount) {
                var visibleRows = document.querySelectorAll('.assessment-row:not(.d-none)');
                visibleCount.textContent = visibleRows.length;
            }
        }

        // ── Clear Answer Functionality ───────────────────────────
        document.addEventListener('click', function(e) {
            var clearBtn = e.target.closest('.clear-answer-btn');
            if (clearBtn) {
                var caseId = clearBtn.dataset.caseId;
                var row = document.getElementById('row-' + caseId);
                if (row) {
                    // Remove has-previous-answer class and add needs-answer
                    row.classList.remove('has-previous-answer');
                    row.classList.add('needs-answer');
                    row.dataset.hasAnswer = 'false';
                    
                    // Update the Has Answer column badge
                    var hasAnswerCol = row.querySelector('.has-answer-col');
                    if (hasAnswerCol) {
                        hasAnswerCol.innerHTML = '<span class="badge bg-soft-secondary text-secondary rounded-pill border border-secondary border-opacity-10" title="' + (app.dataset.noAnswerLabel || 'No answer yet') + '"><i class="bi bi-circle"></i></span>';
                    }
                    
                    // Clear the Clear column
                    var clearCol = row.querySelector('.clear-answer-col');
                    if (clearCol) {
                        clearCol.innerHTML = '<span class="opacity-25">—</span>';
                    }
                    
                    // Clear other result columns
                    var statusCol = row.querySelector('.status-col');
                    if (statusCol) {
                        statusCol.innerHTML = '<span class="badge bg-soft-secondary text-secondary rounded-pill">' + (app.dataset.pendingLabel || 'Pending') + '</span>';
                    }
                    
                    var latencyCol = row.querySelector('.latency-col');
                    if (latencyCol) {
                        latencyCol.textContent = '---';
                    }
                    
                    var actualRoute = row.querySelector('.actual-route');
                    if (actualRoute) {
                        actualRoute.innerHTML = '<span class="opacity-50">---</span>';
                    }
                    
                    // Update Previous, Trend, Runs columns
                    var prevCol = row.querySelector('.previous-result');
                    if (prevCol) prevCol.innerHTML = '<span class="opacity-50 small">---</span>';
                    
                    var trendCol = row.querySelector('.status-trend');
                    if (trendCol) trendCol.innerHTML = '<span class="opacity-50 small">---</span>';
                    
                    var runsCol = row.querySelector('.total-runs');
                    if (runsCol) runsCol.innerHTML = '<span class="opacity-50 small">0</span>';
                    
                    // Disable view logic button
                    var logicBtn = row.querySelector('.btn-view-logic');
                    if (logicBtn) {
                        logicBtn.disabled = true;
                        logicBtn.classList.remove('btn-soft-success', 'btn-soft-danger');
                        logicBtn.classList.add('btn-soft-secondary');
                    }
                    
                    // Store in localStorage that this answer was cleared
                    var clearedAnswers = JSON.parse(localStorage.getItem('clearedAnswers') || '[]');
                    if (!clearedAnswers.includes(caseId)) {
                        clearedAnswers.push(caseId);
                        localStorage.setItem('clearedAnswers', JSON.stringify(clearedAnswers));
                    }

                    updateSummaryCounts();
                }
            }
        });

        // Apply cleared answers on page load
        (function applyClearedAnswers() {
            var clearedAnswers = JSON.parse(localStorage.getItem('clearedAnswers') || '[]');
            clearedAnswers.forEach(function(caseId) {
                var clearBtn = document.querySelector('.clear-answer-btn[data-case-id="' + caseId + '"]');
                if (clearBtn) {
                    clearBtn.click();
                }
            });
        })();

        // ── Column Sorting ────────────────────────────────────────
        var sortableHeaders = document.querySelectorAll('.sortable');
        for (var s = 0; s < sortableHeaders.length; s++) {
            sortableHeaders[s].addEventListener('click', handleSortClick);
        }

        function handleSortClick() {
            var sortKey = this.dataset.sort;
            var isAsc = !this.classList.contains('asc');

            // Clear all sort indicators
            var allSortable = document.querySelectorAll('.sortable');
            for (var i = 0; i < allSortable.length; i++) {
                allSortable[i].classList.remove('asc', 'desc');
            }
            this.classList.add(isAsc ? 'asc' : 'desc');

            sortCatalog(sortKey, isAsc);
        }

        // ── Button Handlers ───────────────────────────────────────
        if (runSelectedButton) {
            runSelectedButton.addEventListener('click', function () {
                runAssessment(true);
            });
        }
        if (runButton) {
            runButton.addEventListener('click', function () {
                runAssessment(false);
            });
        }

        // ── Hydrate Latest Run ────────────────────────────────────
        hydrateLatestRun();

        // ── Suite Group Headers (DataTables-aware) ───────────────
        // We can't put a colspan <tr> in the rendered tbody because DataTables
        // expects every row to match the column count and warns otherwise. Instead,
        // hook the draw event and inject DOM-only header rows between suite changes.
        installSuiteGroupHeaders();

        // ── Details Button Delegation ─────────────────────────────
        document.addEventListener('click', function (e) {
            var btn = e.target.closest('.btn-view-details');
            if (!btn) return;

            var d = btn.dataset;
            var question = d.question || '';
            var answer = d.answer || '';
            var code = d.code || '';
            var suite = d.suite || '';
            var category = d.category || '';
            var difficulty = d.difficulty || '';
            var hasAnswer = d.hasAnswer === 'true';
            var hasLatestResult = d.hasLatestResult === 'true';
            var isSuccessRaw = d.isSuccess || '';
            var expectedSql = d.expectedSql || '';
            var generatedSql = d.generatedSql || '';
            var detail = d.detail || '';
            var traceId = d.traceId || '';
            var traceSessionId = d.traceSessionId || '';
            var latencyMs = d.latencyMs || '';
            var modelName = d.modelName || '';
            var latestRunAt = d.latestRunAt || '';
            var totalRuns = d.totalRuns || '0';

            var noAnswerLabel = app.dataset.noAnswerLabel || 'No answer yet';
            var confirmBtn = app.dataset.understoodLabel || 'Close';

            var html = '<div class="text-start" style="font-size: 0.85rem;">';

            // ── Header badges ──
            var meta = [];
            if (code) meta.push('<span class="badge bg-soft-primary text-primary rounded-pill"><i class="bi bi-hash"></i>' + escapeHtml(code) + '</span>');
            if (suite) meta.push('<span class="badge bg-soft-info text-info rounded-pill"><i class="bi bi-collection me-1"></i>' + escapeHtml(suite) + '</span>');
            if (category) meta.push('<span class="badge bg-soft-secondary text-secondary rounded-pill">' + escapeHtml(category) + '</span>');
            if (difficulty) meta.push('<span class="badge bg-soft-warning text-warning rounded-pill">' + escapeHtml(difficulty) + '</span>');
            if (hasLatestResult && isSuccessRaw === 'true') meta.push('<span class="badge bg-success rounded-pill"><i class="bi bi-check-circle-fill me-1"></i>Pass</span>');
            if (hasLatestResult && isSuccessRaw === 'false') meta.push('<span class="badge bg-danger rounded-pill"><i class="bi bi-x-circle-fill me-1"></i>Fail</span>');
            if (meta.length) {
                html += '<div class="d-flex flex-wrap gap-2 mb-3">' + meta.join(' ') + '</div>';
            }

            // ── Question ──
            html += '<div class="mb-3">' +
                    '<label class="small text-muted text-uppercase fw-bold d-block mb-1"><i class="bi bi-question-circle me-1"></i>Question</label>' +
                    '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="line-height: 1.5;">' + escapeHtml(question) + '</div>' +
                    '</div>';

            // ── Answer ──
            html += '<div class="mb-3"><label class="small text-muted text-uppercase fw-bold d-block mb-1"><i class="bi bi-chat-left-text me-1"></i>Answer</label>';
            if (hasAnswer && answer) {
                html += '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="font-style: italic; line-height: 1.5;">"' + escapeHtml(answer) + '"</div>';
            } else {
                html += '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5 text-muted small fst-italic">' + escapeHtml(noAnswerLabel) + ' — run this case to see the response.</div>';
            }
            html += '</div>';

            // ── Run metadata (only when there is a trace) ──
            if (latencyMs || modelName || latestRunAt || (totalRuns && totalRuns !== '0')) {
                html += '<div class="mb-3"><label class="small text-muted text-uppercase fw-bold d-block mb-1"><i class="bi bi-speedometer2 me-1"></i>Run Metadata</label>' +
                        '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5">' +
                        '<div class="row g-2">';
                if (latencyMs) {
                    var latSecs = (parseInt(latencyMs, 10) / 1000).toFixed(2);
                    html += '<div class="col-6 col-md-3"><div class="text-muted x-small text-uppercase">Latency</div><div class="fw-bold">' + latSecs + 's</div></div>';
                }
                if (modelName) {
                    html += '<div class="col-6 col-md-3"><div class="text-muted x-small text-uppercase">Model / Intent</div><div class="fw-bold text-truncate" title="' + escapeHtml(modelName) + '">' + escapeHtml(modelName) + '</div></div>';
                }
                if (latestRunAt) {
                    html += '<div class="col-6 col-md-3"><div class="text-muted x-small text-uppercase">Last Run</div><div class="fw-bold small">' + escapeHtml(latestRunAt) + '</div></div>';
                }
                if (totalRuns) {
                    html += '<div class="col-6 col-md-3"><div class="text-muted x-small text-uppercase">Total Runs</div><div class="fw-bold">' + escapeHtml(totalRuns) + '</div></div>';
                }
                html += '</div></div></div>';
            }

            // ── SQL comparison ──
            if (expectedSql || generatedSql) {
                html += '<div class="mb-3"><label class="small text-muted text-uppercase fw-bold d-block mb-1"><i class="bi bi-code-slash me-1"></i>SQL</label>' +
                        '<div class="row g-2">' +
                        '<div class="col-md-6"><div class="x-small text-muted fw-bold mb-1">Expected (Golden)</div>' +
                        '<pre class="p-2 mb-0 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="white-space: pre-wrap; word-break: break-word; max-height: 240px; overflow:auto; font-size: 0.75rem;">' +
                        escapeHtml(expectedSql || '(no golden SQL on this case)') + '</pre></div>' +
                        '<div class="col-md-6"><div class="x-small text-muted fw-bold mb-1">Generated</div>' +
                        '<pre class="p-2 mb-0 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="white-space: pre-wrap; word-break: break-word; max-height: 240px; overflow:auto; font-size: 0.75rem;">' +
                        escapeHtml(generatedSql || '(no SQL captured)') + '</pre></div>' +
                        '</div></div>';
            }

            // ── Failure detail / reasoning path ──
            if (detail) {
                var detailIsFailure = hasLatestResult && isSuccessRaw === 'false';
                html += '<div class="mb-3"><label class="small text-muted text-uppercase fw-bold d-block mb-1">' +
                        '<i class="bi ' + (detailIsFailure ? 'bi-exclamation-triangle text-warning' : 'bi-list-check') + ' me-1"></i>' +
                        (detailIsFailure ? 'Failure Detail' : 'Reasoning Path') + '</label>' +
                        '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5 small" style="line-height: 1.5;">' +
                        escapeHtml(detail) + '</div></div>';
            }

            // ── Investigation link ──
            if (traceSessionId) {
                var investUrl = '/AiAnalysis/CopilotAnalysis/TraceInvestigation?sessionId=' + encodeURIComponent(traceSessionId);
                html += '<div class="mt-3 d-flex flex-wrap gap-2">' +
                        '<a href="' + investUrl + '" class="btn btn-sm btn-soft-primary rounded-pill px-3 fw-bold" target="_blank" rel="noopener">' +
                        '<i class="bi bi-search me-1"></i>Open Investigation' +
                        (traceId ? ' <span class="text-muted ms-1">#' + escapeHtml(traceId) + '</span>' : '') +
                        '</a></div>';
            }

            html += '</div>';

            showDialog('info', 'Question Details', html, confirmBtn, 'xl');
        });

        // ── Logic Button Delegation ───────────────────────────────
        document.addEventListener('click', function (e) {
            var btn = e.target.closest('.btn-view-logic');
            if (!btn) return;
            var logic = btn.dataset.logic || '';
            var answer = btn.dataset.answer || '';
            var expectedSql = btn.dataset.expectedSql || '';
            var generatedSql = btn.dataset.generatedSql || '';
            if (!logic && !expectedSql && !generatedSql) return;

            var title = app.dataset.logicTitle || 'Execution Logic';
            var pathLabel = app.dataset.logicPath || 'Reasoning Path';
            var previewLabel = app.dataset.logicPreview || 'Response Preview';
            var confirmBtn = app.dataset.understoodLabel || 'Understood';

            var html = '<div class="text-start" style="font-size: 0.85rem;">';

            if (expectedSql || generatedSql) {
                html += '<div class="row g-2 mb-3">';
                html += '<div class="col-md-6"><label class="small text-muted text-uppercase fw-bold">Expected SQL</label>' +
                        '<pre class="p-2 mb-0 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="white-space: pre-wrap; word-break: break-word; max-height: 280px; overflow:auto;">' +
                        escapeHtml(expectedSql || '(no golden SQL on this case)') + '</pre></div>';
                html += '<div class="col-md-6"><label class="small text-muted text-uppercase fw-bold">Generated SQL</label>' +
                        '<pre class="p-2 mb-0 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="white-space: pre-wrap; word-break: break-word; max-height: 280px; overflow:auto;">' +
                        escapeHtml(generatedSql || '(no SQL captured)') + '</pre></div>';
                html += '</div>';
            }

            if (logic) {
                html += '<div class="mb-3">' +
                        '<label class="small text-muted text-uppercase fw-bold">' + escapeHtml(pathLabel) + '</label>' +
                        '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5">' + escapeHtml(logic) + '</div>' +
                        '</div>';
            }

            if (answer) {
                html += '<div><label class="small text-muted text-uppercase fw-bold">' + escapeHtml(previewLabel) + '</label>' +
                        '<div class="p-3 rounded bg-dark bg-opacity-25 border border-white border-opacity-5" style="font-style: italic;">"' + escapeHtml(answer) + '"</div></div>';
            }

            html += '</div>';
            showDialog('info', title, html, confirmBtn, 'xl');
        });

        // ═══════════════════════════════════════════════════════════
        // FUNCTIONS
        // ═══════════════════════════════════════════════════════════

        function updateRunSelectedVisibility() {
            var selectAllCheckbox = document.getElementById('selectAllCases');
            var allVisibleSelectors = getVisibleCaseSelectors(false);
            var checkedVisibleSelectors = getVisibleCaseSelectors(true);
            var totalSelectedCount = document.querySelectorAll('.case-selector:checked').length;

            if (selectAllCheckbox) {
                selectAllCheckbox.checked = allVisibleSelectors.length > 0 && allVisibleSelectors.length === checkedVisibleSelectors.length;
                selectAllCheckbox.indeterminate = checkedVisibleSelectors.length > 0 && checkedVisibleSelectors.length < allVisibleSelectors.length;
            }

            if (runSelectedButton) {
                runSelectedButton.classList.toggle('d-none', totalSelectedCount === 0);
                var span = runSelectedButton.querySelector('span');
                if (span) span.textContent = 'Run Selected Page (' + totalSelectedCount + ')';
                runSelectedButton.title = 'Run only the selected scenarios currently loaded on this page.';
            }

            var selectionInfo = document.getElementById('selectionInfo');
            if (selectionInfo) {
                selectionInfo.classList.toggle('d-none', totalSelectedCount === 0);
                selectionInfo.classList.toggle('d-flex', totalSelectedCount > 0);
            }
            
            var countSelected = document.getElementById('countSelected');
            if (countSelected) {
                countSelected.textContent = totalSelectedCount;
            }
        }

        function getVisibleCaseSelectors(checkedOnly) {
            return getVisibleAssessmentRows()
                .map(function (row) { return row.querySelector('.case-selector'); })
                .filter(function (selector) {
                    return selector && (!checkedOnly || selector.checked);
                });
        }

        function getVisibleAssessmentRows() {
            return Array.from(document.querySelectorAll('.assessment-row')).filter(function (row) {
                return !row.classList.contains('d-none') && row.offsetParent !== null;
            });
        }

        function sortCatalog(key, asc) {
            var tbody = document.getElementById('testResultsBody');
            if (!tbody) return;

            // Get all data rows (not headers)
            var allRows = Array.from(tbody.querySelectorAll('.assessment-row'));
            
            // Get all group headers with their current category
            var headers = Array.from(tbody.querySelectorAll('.table-group-header'));
            var headerData = headers.map(function(h) {
                var categoryCell = h.querySelector('td:nth-child(2)');
                var categoryName = categoryCell ? categoryCell.textContent.trim() : '';
                return { element: h, category: categoryName };
            });

            // Sort all rows globally
            allRows.sort(function (a, b) {
                var valA, valB;
                switch (key) {
                    case 'scenario':
                        valA = getSortText(a, '.scenario-col .fw-semibold');
                        valB = getSortText(b, '.scenario-col .fw-semibold');
                        return compareValues(valA, valB, asc);
                    case 'category':
                        valA = a.dataset.category || '';
                        valB = b.dataset.category || '';
                        return compareValues(valA, valB, asc);
                    case 'difficulty':
                        valA = getSortText(a, '.difficulty-col .badge');
                        valB = getSortText(b, '.difficulty-col .badge');
                        return compareDifficulty(valA, valB, asc);
                    case 'status':
                        valA = getSortText(a, '.status-col .badge');
                        valB = getSortText(b, '.status-col .badge');
                        return compareStatus(valA, valB, asc);
                    default:
                        return 0;
                }
            });

            // Clear tbody
            while (tbody.firstChild) {
                tbody.removeChild(tbody.firstChild);
            }

            // If sorting by category, group rows under their headers
            if (key === 'category') {
                // Group rows by category
                var rowsByCategory = {};
                allRows.forEach(function(row) {
                    var cat = row.dataset.category || 'General';
                    if (!rowsByCategory[cat]) {
                        rowsByCategory[cat] = [];
                    }
                    rowsByCategory[cat].push(row);
                });

                // Re-append headers and their rows
                headerData.forEach(function(header) {
                    tbody.appendChild(header.element);
                    var catRows = rowsByCategory[header.category] || [];
                    catRows.forEach(function(row) {
                        tbody.appendChild(row);
                    });
                    delete rowsByCategory[header.category];
                });

                // Append any remaining rows (new categories)
                Object.keys(rowsByCategory).forEach(function(cat) {
                    rowsByCategory[cat].forEach(function(row) {
                        tbody.appendChild(row);
                    });
                });
            } else {
                // For other sorts, keep original group structure but with sorted rows
                // We'll still need to maintain groups but rows within are sorted
                var currentCategory = '';
                var headerIndex = 0;
                
                // First, append first header
                if (headerData.length > 0) {
                    tbody.appendChild(headerData[0].element);
                    currentCategory = headerData[0].category;
                    headerIndex = 1;
                }
                
                // Then append rows in sorted order, adding headers when category changes
                allRows.forEach(function(row) {
                    var rowCat = row.dataset.category || '';
                    
                    // If this row's category doesn't match current, try to find and append the right header
                    if (rowCat !== currentCategory && headerIndex < headerData.length) {
                        // Look for matching header
                        for (var i = headerIndex; i < headerData.length; i++) {
                            if (headerData[i].category === rowCat) {
                                tbody.appendChild(headerData[i].element);
                                currentCategory = rowCat;
                                headerIndex = i + 1;
                                break;
                            }
                        }
                    }
                    
                    tbody.appendChild(row);
                });
            }
        }

        function compareValues(valA, valB, asc) {
            if (valA < valB) return asc ? -1 : 1;
            if (valA > valB) return asc ? 1 : -1;
            return 0;
        }

        function compareDifficulty(valA, valB, asc) {
            var difficultyOrder = {'Easy': 1, 'Medium': 2, 'Hard': 3, 'Complicated': 4};
            var orderA = difficultyOrder[valA] || 999;
            var orderB = difficultyOrder[valB] || 999;
            return asc ? orderA - orderB : orderB - orderA;
        }

        function compareStatus(valA, valB, asc) {
            var statusOrder = {'Pass': 1, 'Fail': 2, 'Pending': 3, 'Running': 4};
            var orderA = statusOrder[valA] || 999;
            var orderB = statusOrder[valB] || 999;
            return asc ? orderA - orderB : orderB - orderA;
        }

        function getSortText(row, selector) {
            var el = row.querySelector(selector);
            return el ? el.textContent.toLowerCase().trim() : '';
        }

        function getSortNumber(row, selector) {
            var el = row.querySelector(selector);
            if (!el) return 0;
            var text = el.textContent.replace('ms', '').trim();
            var num = parseInt(text, 10);
            return isNaN(num) ? 0 : num;
        }

        function getSortTrend(row) {
            var el = row.querySelector('.status-trend i');
            if (!el) return 5;
            if (el.classList.contains('bi-arrow-up-circle-fill')) return 1;   // Improved
            if (el.classList.contains('bi-dash-circle-fill')) return 2;       // Same
            if (el.classList.contains('bi-arrow-down-circle-fill')) return 3; // Regressed
            if (el.classList.contains('bi-star-fill')) return 4;              // New
            return 5;
        }

        function runAssessment(selectedOnly) {
            if (!runUrl) return;

            var caseIds = selectedOnly
                ? Array.from(document.querySelectorAll('.case-selector:checked')).map(function (cb) { return cb.value; })
                : null;
            if (selectedOnly && (!caseIds || caseIds.length === 0)) {
                showDialog('warning', 'No scenarios selected', 'Select one or more visible scenarios before using Run Selected Page.', 'OK');
                return;
            }

            var sessionId = sessionSelector && sessionSelector.value ? parseInt(sessionSelector.value, 10) : null;
            var totalCount = selectedOnly
                ? (caseIds ? caseIds.length : 0)
                : parseInt(app.dataset.totalCases || '0', 10);
            activeRunCaseIds = caseIds && caseIds.length ? caseIds.slice() : null;

            setRunningState(true);
            resetRows(caseIds);
            
            // Reset global stats
            globalRunStats = {
                completedCount: 0,
                successCount: 0,
                totalLatency: 0,
                totalCases: totalCount
            };
            
            startProgressSimulation(totalCount, selectedOnly);

            const token = document.querySelector('#csrf-form input[name="__RequestVerificationToken"]')?.value || '';

            runAbortController = new AbortController();
            const signal = runAbortController.signal;

            fetch(runUrl, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': token,
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body: JSON.stringify({
                    caseIds: caseIds,
                    sessionId: sessionId,
                    // Multi-suite: read the currently-checked suite filenames from the dropdown
                    // so the server can re-apply the selection on this fresh request scope.
                    // Without this, the Scoped handler's _activeSuiteFiles is null on the run
                    // request and the run falls back to "newest single file" (~10 questions).
                    suiteFiles: (function () {
                        var checks = document.querySelectorAll('.suite-checkbox:checked');
                        return checks.length > 0
                            ? Array.from(checks).map(function (cb) { return cb.value; })
                            : null;
                    })()
                }),
                signal: signal
            })
                .then(function (response) {
                    if (!response.ok) throw new Error('Network response was not ok');
                    return response.json();
                })
                .then(function (payload) {
                    const success = (payload.success === true || payload.Success === true);
                    if (!success || !(payload.data || payload.Data)) {
                        throw new Error((payload.error || payload.Error || payload.message || payload.Message) || 'Assessment request failed.');
                    }

                    const data = payload.data || payload.Data;
                    const newSessionId = data.sessionId || data.SessionId;
                    const serverTotalCases = data.totalCases || data.TotalCases || totalCount;
                    const runId = data.runId || data.RunId;
                    
                    // Update the progress container with the accurate total from the server
                    if (progressContainer) {
                        progressContainer.dataset.totalCases = serverTotalCases.toString();
                    }
                    if (progressCount) progressCount.textContent = '0 / ' + serverTotalCases;

                    if (!sessionId && sessionSelector) {
                        const opt = document.createElement('option');
                        opt.value = newSessionId;
                        opt.textContent = 'Active Session #' + newSessionId;
                        opt.selected = true;
                        sessionSelector.appendChild(opt);
                    }

                    startRealProgressTracking(newSessionId, activeRunCaseIds, runId);
                })
                .catch(function (error) {
                    if (error.name === 'AbortError') {
                        showDialog('info', 'Cancelled', 'Assessment run was aborted.', 'OK');
                    } else {
                        showDialog('error', 'Error', error.message || 'Assessment request failed.', 'OK');
                    }
                    setRunningState(false);
                    stopProgressSimulation();
                    stopRealProgressTracking();
                    activeRunCaseIds = null;
                });
        }

        function setRunningState(isRunning) {
            runButton.disabled = isRunning;
            if (runSelectedButton) runSelectedButton.disabled = isRunning;
            if (stopButton) {
                stopButton.classList.toggle('d-none', !isRunning);
            }

            var rtlPrefix = document.documentElement.dir === 'rtl' ? 'ms-' : 'me-';
            var spinner = '<span class="spinner-border spinner-border-sm ' + rtlPrefix + '2" role="status" aria-hidden="true"></span>';

            runButton.innerHTML = isRunning
                ? spinner + '<span>' + escapeHtml(runningLabel) + '</span>'
                : '<i class="bi bi-play-fill ' + rtlPrefix + '1"></i><span>' + escapeHtml(runLabel) + '</span>';

            if (runSelectedButton) {
                var totalSelectedCount = document.querySelectorAll('.case-selector:checked').length;
                runSelectedButton.innerHTML = isRunning
                    ? spinner + '<span>Running (' + totalSelectedCount + ')</span>'
                    : '<i class="bi bi-play-fill ' + rtlPrefix + '1"></i><span>Run Selected Page (' + totalSelectedCount + ')</span>';
            }

            if (summary && isRunning) {
                summary.textContent = runningLabel;
            }

            if (progressContainer) {
                if (isRunning) {
                    progressContainer.classList.remove('d-none');
                    progressContainer.classList.add('animate__animated', 'animate__fadeInDown');
                } else {
                    var statusElement = document.getElementById('assessmentLiveStatus');
                    if (statusElement) statusElement.classList.add('d-none');
                    
                    setTimeout(function () {
                        progressContainer.classList.add('animate__fadeOutUp');
                        setTimeout(function () {
                            progressContainer.classList.add('d-none');
                            progressContainer.classList.remove('animate__animated', 'animate__fadeInDown', 'animate__fadeOutUp');
                        }, 500);
                    }, 2000);
                }
            }
        }

        function updateLiveStatus(status) {
            var statusElement = document.getElementById('assessmentLiveStatus');
            if (statusElement) {
                statusElement.textContent = status;
                statusElement.classList.remove('d-none');
            }
        }

        function startRealProgressTracking(manualSessionId, caseIds, runId) {
            stopRealProgressTracking();

            var sessionId = manualSessionId || (sessionSelector && sessionSelector.value ? parseInt(sessionSelector.value, 10) : null);
            if (!sessionId) return;

            if (hubConnection && hubConnection.state === signalR.HubConnectionState.Connected) {
                hubConnection.invoke("JoinSession", sessionId.toString())
                    .catch(err => console.error("Error joining SignalR session group:", err));
            } else {
                // Fallback to polling if SignalR is not available or connected
                progressInterval = setInterval(function() {
                    fetch(buildAssessmentUrl('/AiAnalysis/GetAssessmentProgress', sessionId, caseIds, runId))
                        .then(function(response) { return response.json(); })
                        .then(function(data) {
                            var completedCount = data.completed || 0;
                            var total = parseInt(progressContainer.dataset.totalCases || '1', 10);
                            // Server may include the currently-running case code; pass it
                            // through so the progress label can show "AGG-09" instead of just
                            // a generic phase label.
                            var currentCode = data.currentCaseCode || data.CurrentCaseCode || null;
                            updateProgressUI(completedCount, total, sessionId, caseIds, currentCode);
                        })
                        .catch(function(err) { console.error('Progress polling error:', err); });
                }, 3000);
            }
        }


        function stopRealProgressTracking() {
            if (progressInterval) {
                clearInterval(progressInterval);
                progressInterval = null;
            }
            
            var sessionId = (sessionSelector && sessionSelector.value ? sessionSelector.value : null);
            if (hubConnection && hubConnection.state === signalR.HubConnectionState.Connected && sessionId) {
                hubConnection.invoke("LeaveSession", sessionId.toString())
                    .catch(err => console.error("Error leaving SignalR session group:", err));
            }
        }


        function updateProgressUI(completedCases, totalCases, sessionId, caseIds, currentCaseCode) {
            if (!progressContainer) return;

            progressContainer.dataset.completedCases = completedCases.toString();
            var progressPercent = totalCases > 0 ? (completedCases / totalCases) * 100 : 0;

            if (progressBar) progressBar.style.width = Math.min(progressPercent, 100) + '%';
            if (progressCount) progressCount.textContent = completedCases + ' / ' + totalCases;

            // Mirror progress to the TOP KPI cards (Forensic Coverage). Pre-fix bug: the top
            // card stayed at "0 / 75" while the bottom bar showed "2 / 75" because only the
            // row-streaming path updated the top. Now both update in lockstep.
            var statCompletedTop = document.getElementById('statCompleted');
            var statTotalTop = document.getElementById('statTotal');
            var statProgressBarTop = document.getElementById('statProgressBar');
            if (statCompletedTop) statCompletedTop.textContent = completedCases;
            if (statTotalTop) statTotalTop.textContent = totalCases;
            if (statProgressBarTop) statProgressBarTop.style.width = Math.min(progressPercent, 100) + '%';

            if (progressStatus) {
                // Include the current case code (e.g. "AGG-09") when the polling endpoint
                // returns it — gives the user concrete "what's running NOW" feedback instead of
                // generic "Analyzing reasonings...". Fallback to the phase label when no code
                // is available (e.g. between cases).
                var phaseLabel;
                if (completedCases >= totalCases && totalCases > 0) {
                    phaseLabel = app.dataset.assessmentCompleteTitle || 'Assessment Complete';
                } else if (progressPercent > 75) {
                    phaseLabel = app.dataset.statusVetting || 'Vetting results...';
                } else if (progressPercent > 50) {
                    phaseLabel = app.dataset.statusExecuting || 'Executing plans...';
                } else if (progressPercent > 25) {
                    phaseLabel = app.dataset.statusAnalyzing || 'Analyzing reasonings...';
                } else {
                    phaseLabel = app.dataset.statusOrchestrating || 'Orchestrating suite...';
                }
                progressStatus.textContent = currentCaseCode
                    ? phaseLabel + ' — running ' + currentCaseCode
                    : phaseLabel;
            }

            if (completedCases >= totalCases && totalCases > 0) {
                stopRealProgressTracking();
                fetchFinalResults(sessionId, caseIds);
            }
        }

        function fetchFinalResults(sessionId, caseIds) {
            if (summary) {
                summary.innerHTML = '<i class="bi bi-check-circle-fill text-success me-1"></i>Finalizing assessment run...';
            }

            fetch(buildAssessmentUrl('/AiAnalysis/GetLatestRun', sessionId, caseIds), {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
            .then(function(r) { return r.json(); })
            .then(function(payload) {
                if (payload.success && payload.data) {
                    updateSummary(payload.data);
                    updateRows(payload.data.results || [], { updateStats: false });
                }
            })
            .catch(function(e) { console.error('Failed to fetch final results:', e); })
            .finally(function() {
                setRunningState(false);
                stopProgressSimulation();
                activeRunCaseIds = null;
            });
        }

        function buildAssessmentUrl(path, sessionId, caseIds, runId) {
            var url = new URL(path, window.location.origin);
            url.searchParams.set('sessionId', sessionId);
            if (caseIds && caseIds.length) {
                url.searchParams.set('caseIds', caseIds.join(','));
            }
            if (runId) {
                url.searchParams.set('runId', runId);
            }
            return url.toString();
        }

        function updateProgressIncrement() {
            var totalCases = parseInt(progressContainer.dataset.totalCases || '0', 10);
            var completedCases = parseInt(progressContainer.dataset.completedCases || '0', 10) + 1;
            progressContainer.dataset.completedCases = completedCases.toString();
            var progressPercent = totalCases > 0 ? (completedCases / totalCases) * 100 : 0;
            if (progressBar) progressBar.style.width = Math.min(progressPercent, 100) + '%';
            if (progressCount) progressCount.textContent = completedCases + ' / ' + totalCases;
            if (progressStatus) {
                if (completedCases >= totalCases && totalCases > 0) {
                    progressStatus.textContent = app.dataset.assessmentCompleteTitle || 'Assessment Complete';
                } else if (progressPercent > 75) {
                    progressStatus.textContent = app.dataset.statusVetting || 'Vetting results...';
                } else if (progressPercent > 50) {
                    progressStatus.textContent = app.dataset.statusExecuting || 'Executing plans...';
                } else if (progressPercent > 25) {
                    progressStatus.textContent = app.dataset.statusAnalyzing || 'Analyzing reasonings...';
                } else {
                    progressStatus.textContent = app.dataset.statusOrchestrating || 'Orchestrating suite...';
                }
            }
            if (completedCases >= totalCases && totalCases > 0) {
                completeProgressTracking();
            }
        }

        function completeProgressTracking() {
            clearInterval(progressInterval);
            var totalCases = parseInt(progressContainer.dataset.totalCases || '0', 10);
            if (progressBar) progressBar.style.width = '100%';
            if (progressStatus) progressStatus.textContent = app.dataset.assessmentCompleteTitle || 'Assessment Complete';
            if (progressCount) progressCount.textContent = totalCases + ' / ' + totalCases;
        }

        function stopProgressTracking() {
            clearInterval(progressInterval);
            progressInterval = null;
        }

        // Legacy wrappers — do NOT pass totalCount as sessionId
        function startProgressSimulation(totalCount, selectedOnly) {
            // Only set totalCases on the container; real polling starts after session ID is known
            if (progressContainer) {
                progressContainer.dataset.totalCases = (totalCount || 0).toString();
                progressContainer.dataset.completedCases = '0';
            }
            if (progressBar) progressBar.style.width = '0%';
            if (progressCount) progressCount.textContent = '0 / ' + (totalCount || 0);
            if (progressStatus) progressStatus.textContent = app.dataset.statusOrchestrating || 'Orchestrating suite...';
        }

        function completeProgressSimulation() {
            completeProgressTracking();
        }

        function stopProgressSimulation() {
            stopProgressTracking();
        }

        function resetRows(onlyIds) {
            var rows;
            if (onlyIds) {
                rows = [];
                for (var i = 0; i < onlyIds.length; i++) {
                    var row = document.getElementById('row-' + onlyIds[i]);
                    if (row) rows.push(row);
                }
            } else {
                rows = document.querySelectorAll('#testResultsBody tr[data-case-id]');
            }

            for (var j = 0; j < rows.length; j++) {
                var r = rows[j];
                var status = r.querySelector('.status-col');
                var route = r.querySelector('.actual-route');
                var latency = r.querySelector('.latency-col');
                var answerPreview = r.querySelector('.assessment-answer-preview');
                var previousResult = r.querySelector('.previous-result');
                var statusTrend = r.querySelector('.status-trend');
                var totalRuns = r.querySelector('.total-runs');
                var logicBtn = r.querySelector('.btn-view-logic');

                if (status) {
                    status.innerHTML = '<span class="spinner-grow spinner-grow-sm text-primary" role="status" aria-hidden="true"></span>';
                }
                if (route) route.textContent = '---';
                if (latency) latency.textContent = '---';
                if (previousResult) previousResult.innerHTML = '<span class="opacity-50 small">---</span>';
                if (statusTrend) statusTrend.innerHTML = '<span class="opacity-50 small">---</span>';
                if (totalRuns) totalRuns.innerHTML = '<span class="opacity-50 small">0</span>';
                if (answerPreview) {
                    answerPreview.textContent = '';
                    answerPreview.classList.add('d-none');
                    answerPreview.classList.remove('text-success', 'text-danger');
                }
                if (logicBtn) {
                    logicBtn.disabled = true;
                    logicBtn.classList.remove('btn-soft-success', 'btn-soft-danger');
                    logicBtn.classList.add('btn-soft-secondary');
                }
            }

            if (statusRate) statusRate.textContent = '-';
            if (statusLatency) statusLatency.textContent = '-';
            
            // Reset new card elements
            var statProgress = document.getElementById('statProgress');
            var statCompleted = document.getElementById('statCompleted');
            var statProgressBar = document.getElementById('statProgressBar');
            var statPassed = document.getElementById('statPassed');
            var statFailed = document.getElementById('statFailed');
            var statAvgLatency = document.getElementById('statAvgLatency');
            
            if (statProgress) statProgress.textContent = '0%';
            if (statCompleted) statCompleted.textContent = '0';
            if (statProgressBar) statProgressBar.style.width = '0%';
            if (statPassed) statPassed.textContent = '0';
            if (statFailed) statFailed.textContent = '0';
            if (statAvgLatency) statAvgLatency.textContent = '-';
        }

        function updateSummary(runSummary) {
            var percent = Number(runSummary.successRate || 0) * 100;
            var totalCases = Number(runSummary.totalCases || 0);
            var successCount = Number(runSummary.successCount || 0);
            var failedCount = Math.max(0, totalCases - successCount);
            var averageLatency = Number(runSummary.averageLatencyMs || 0);

            if (statusRate) {
                statusRate.textContent = percent.toFixed(0) + '%';
                statusRate.className = percent >= 90 ? 'fs-3 fw-bold text-success' : (percent >= 70 ? 'fs-3 fw-bold text-warning' : 'fs-3 fw-bold text-danger');
            }

            var statProgress = document.getElementById('statProgress');
            var statCompleted = document.getElementById('statCompleted');
            var statTotal = document.getElementById('statTotal');
            var statProgressBar = document.getElementById('statProgressBar');
            var statPassed = document.getElementById('statPassed');
            var statFailed = document.getElementById('statFailed');

            if (statProgress) statProgress.textContent = percent.toFixed(0) + '%';
            if (statCompleted) statCompleted.textContent = totalCases;
            if (statTotal) statTotal.textContent = totalCases;
            if (statProgressBar) statProgressBar.style.width = percent + '%';
            if (statPassed) statPassed.textContent = successCount;
            if (statFailed) statFailed.textContent = failedCount;

            if (statusLatency) {
                statusLatency.textContent = averageLatency + 'ms';
            }

            if (summary) {
                summary.innerHTML = '<i class="bi bi-check-circle-fill text-success me-1"></i>Run #' + runSummary.summaryId + ' finished with ' + runSummary.successCount + '/' + runSummary.totalCases + ' passing cases.';
            }
        }

        function updateRows(results, options) {
            var shouldUpdateStats = !options || options.updateStats !== false;
            var isIncremental = options && options.isIncremental === true;
            
            var batchSuccess = 0;
            var batchLatency = 0;
            var batchCompleted = 0;

            for (var i = 0; i < results.length; i++) {
                var result = results[i];
                var row = document.getElementById('row-' + result.id);
                if (!row) continue;

                completedCount++;
                if (result.isSuccess) successCount++;
                if (result.latencyMs) totalLatency += result.latencyMs;

                // Update has-answer state based on whether an answer was returned
                var answerText = result.answer || result.answerPreview || '';
                var nowHasAnswer = answerText.length > 0;
                row.dataset.hasAnswer = nowHasAnswer ? 'true' : 'false';
                row.classList.toggle('has-previous-answer', nowHasAnswer);
                row.classList.toggle('needs-answer', !nowHasAnswer);
                var hasAnswerCol = row.querySelector('.has-answer-col');
                if (hasAnswerCol) {
                    hasAnswerCol.innerHTML = nowHasAnswer
                        ? '<span class="badge bg-soft-success text-success rounded-pill border border-success border-opacity-10" title="' + escapeHtml(app.dataset.hasAnswerLabel || 'Has answer') + '"><i class="bi bi-check-circle-fill"></i></span>'
                        : '<span class="badge bg-soft-secondary text-secondary rounded-pill border border-secondary border-opacity-10" title="' + escapeHtml(app.dataset.noAnswerLabel || 'No answer yet') + '"><i class="bi bi-circle"></i></span>';
                }

                var status = row.querySelector('.status-col');
                var route = row.querySelector('.actual-route');
                var latency = row.querySelector('.latency-col');
                var logicBtn = row.querySelector('.btn-view-logic');
                var answerPreview = row.querySelector('.assessment-answer-preview');
                var previousResult = row.querySelector('.previous-result');
                var statusTrend = row.querySelector('.status-trend');
                var totalRuns = row.querySelector('.total-runs');

                if (status) {
                    status.innerHTML = result.isSuccess
                        ? '<span class="badge bg-soft-success text-success rounded-pill border border-success border-opacity-10">' + escapeHtml(passLabel) + '</span>'
                        : '<span class="badge bg-soft-danger text-danger rounded-pill border border-danger border-opacity-10">' + escapeHtml(failLabel) + '</span>';
                }

                if (route) {
                    var parts = [result.actualMode, result.actualIntent, result.actualTool]
                        .filter(function (v) { return v && v.trim().length > 0 && v !== 'none'; });
                    route.innerHTML = parts.length
                        ? '<span class="fw-bold text-main" style="font-size: 0.75rem;">' + parts.join(' | ') + '</span>'
                        : '<span class="opacity-50">' + pendingLabel + '</span>';
                }

                if (latency) {
                    latency.textContent = Number(result.latencyMs || 0) + 'ms';
                }

                // Update historical data columns
                if (previousResult) {
                    if (result.previousResult !== null && result.previousResult !== undefined) {
                        var prevBadge = result.previousResult 
                            ? '<span class="badge bg-soft-success text-success rounded-pill border border-success border-opacity-10">Pass</span>'
                            : '<span class="badge bg-soft-danger text-danger rounded-pill border border-danger border-opacity-10">Fail</span>';
                        previousResult.innerHTML = prevBadge;
                    } else {
                        previousResult.innerHTML = '<span class="opacity-50 small">---</span>';
                    }
                }

                if (statusTrend) {
                    if (result.statusChange && result.statusChange !== '') {
                        var trendIcon = '';
                        var trendClass = '';
                        switch (result.statusChange) {
                            case 'Improved':
                                trendIcon = 'bi-arrow-up-circle-fill';
                                trendClass = 'text-success';
                                break;
                            case 'Regressed':
                                trendIcon = 'bi-arrow-down-circle-fill';
                                trendClass = 'text-danger';
                                break;
                            case 'Same':
                                trendIcon = 'bi-dash-circle-fill';
                                trendClass = 'text-warning';
                                break;
                            case 'New':
                                trendIcon = 'bi-star-fill';
                                trendClass = 'text-info';
                                break;
                        }
                        statusTrend.innerHTML = '<i class="bi ' + trendIcon + ' ' + trendClass + '"></i>';
                        statusTrend.title = result.statusChange;
                    } else {
                        statusTrend.innerHTML = '<span class="opacity-50 small">---</span>';
                    }
                }

                if (totalRuns) {
                    var runsText = result.totalRuns > 0 ? result.totalRuns.toString() : '0';
                    totalRuns.innerHTML = '<span class="small fw-bold">' + runsText + '</span>';
                    
                    if (result.historicalSuccessRate > 0) {
                        totalRuns.title = 'Historical success rate: ' + (result.historicalSuccessRate * 100).toFixed(0) + '%';
                    }
                }

                if (answerPreview) {
                    var answerText = result.answerPreview || result.answer || result.detail || '';
                    if (answerText) {
                        answerPreview.textContent = answerText;
                        answerPreview.classList.remove('d-none', 'text-success', 'text-danger');
                        answerPreview.classList.add(result.isSuccess ? 'text-success' : 'text-danger');
                    } else {
                        answerPreview.textContent = '';
                        answerPreview.classList.add('d-none');
                    }
                }

                if (logicBtn) {
                    logicBtn.disabled = false;
                    logicBtn.dataset.logic = result.detail || '';
                    logicBtn.dataset.answer = result.answer || result.answerPreview || '';
                    logicBtn.classList.remove('btn-soft-secondary');
                    logicBtn.classList.add(result.isSuccess ? 'btn-soft-success' : 'btn-soft-danger');

                    // Add Trace Link if present
                    if (result.traceId) {
                        var traceBtn = row.querySelector('.btn-view-trace');
                        if (!traceBtn) {
                            var btnGroup = row.querySelector('.btn-group');
                            if (btnGroup) {
                                traceBtn = document.createElement('a');
                                traceBtn.className = 'btn btn-soft-secondary btn-sm px-2 border-end border-white border-opacity-10 btn-view-trace';
                                traceBtn.title = 'View Investigation Trace';
                                traceBtn.innerHTML = '<i class="bi bi-diagram-3-fill"></i>';
                                btnGroup.insertBefore(traceBtn, logicBtn);
                            }
                        }
                        if (traceBtn) {
                            traceBtn.href = '/AiAnalysis/InvestigationStory/' + result.traceId;
                        }
                    }
                }

                // Update progress for each completed row
                if (shouldUpdateStats) {
                    if (isIncremental) {
                        updateRealTimeStats(globalRunStats.successCount, globalRunStats.completedCount, globalRunStats.totalLatency);
                    } else {
                        // If not incremental (e.g. final bulk update), use batch totals
                        batchCompleted++;
                        if (result.isSuccess) batchSuccess++;
                        if (result.latencyMs) batchLatency += result.latencyMs;
                        updateRealTimeStats(batchSuccess, batchCompleted, batchLatency);
                    }
                }
            }
        }

        function updateRealTimeStats(successCount, completedCount, totalLatency) {
            var totalCases = parseInt(progressContainer.dataset.totalCases || '0');
            var failedCount = completedCount - successCount;
            var progressPercent = totalCases > 0 ? (completedCount / totalCases) * 100 : 0;
            
            // Update progress card
            var statProgress = document.getElementById('statProgress');
            var statCompleted = document.getElementById('statCompleted');
            var statTotal = document.getElementById('statTotal');
            var statProgressBar = document.getElementById('statProgressBar');
            
            if (statProgress) {
                statProgress.textContent = progressPercent.toFixed(0) + '%';
            }
            if (statCompleted) {
                statCompleted.textContent = completedCount;
            }
            if (statProgressBar) {
                statProgressBar.style.width = progressPercent + '%';
            }
            
            // Update success rate
            if (statusRate && completedCount > 0) {
                var percent = (successCount / completedCount) * 100;
                statusRate.textContent = percent.toFixed(0) + '%';
                statusRate.className = percent >= 90 ? 'fs-3 fw-bold text-success' : (percent >= 70 ? 'fs-3 fw-bold text-warning' : 'fs-3 fw-bold text-danger');
            }

            // Update results card
            var statPassed = document.getElementById('statPassed');
            var statFailed = document.getElementById('statFailed');
            var statAvgLatency = document.getElementById('statAvgLatency');
            
            if (statPassed) {
                statPassed.textContent = successCount;
            }
            if (statFailed) {
                statFailed.textContent = failedCount;
            }
            
            // Update average latency
            if (completedCount > 0) {
                var avgLatency = Math.round(totalLatency / completedCount);
                if (statusLatency) {
                    statusLatency.textContent = avgLatency + 'ms';
                }
                if (statAvgLatency) {
                    statAvgLatency.textContent = avgLatency;
                }
            }
            
            updateSummaryCounts();
            updateVisibleCount();

            // Update summary
            if (summary) {
                summary.innerHTML = '<i class="bi bi-hourglass-split text-primary me-1"></i>Running: ' + completedCount + '/' + totalCases + ' cases processed (' + successCount + ' passed)';
            }
        }

        function installSuiteGroupHeaders() {
            var table = document.getElementById('copilotAssessmentTable');
            if (!table) return;

            function isDtReady() {
                return window.jQuery && jQuery.fn && jQuery.fn.DataTable && jQuery.fn.DataTable.isDataTable(table);
            }

            function render() {
                // Don't touch the tbody until DataTables has parsed it — injecting a colspan
                // row earlier makes DataTables raise "Requested unknown parameter" on init.
                if (!isDtReady()) return;

                var tbody = table.tBodies && table.tBodies[0];
                if (!tbody) return;
                // Remove previously injected headers before re-rendering.
                var prev = tbody.querySelectorAll('tr.suite-group-header');
                for (var p = 0; p < prev.length; p++) prev[p].remove();

                // Only group when rows are in suite-contiguous order. If the user sorted by a
                // different column, suites will interleave and per-row headers would be noise.
                var dt = jQuery(table).DataTable();
                var order = dt.order();
                var suiteColIdx = 2; // Code=1, Suite=2 (0 = checkbox)
                var groupable = !order || order.length === 0 || (order[0] && order[0][0] === suiteColIdx);
                if (!groupable) return;

                var rows = tbody.querySelectorAll('tr.assessment-row');
                var colCount = table.tHead && table.tHead.rows[0] ? table.tHead.rows[0].cells.length : 8;
                var lastSuite = null;
                for (var i = 0; i < rows.length; i++) {
                    var row = rows[i];
                    if (row.classList.contains('d-none')) continue;
                    var suite = row.dataset.suite || '';
                    if (suite === lastSuite) continue;
                    lastSuite = suite;

                    var pretty = row.dataset.suitePretty || (suite || 'Ungrouped');
                    var header = document.createElement('tr');
                    header.className = 'suite-group-header';
                    var td = document.createElement('td');
                    td.colSpan = colCount;
                    td.className = 'px-3 py-2';
                    td.style.background = 'rgba(var(--primary-color-rgb,59,130,246), 0.06)';
                    td.style.borderTop = '1px solid var(--border-color)';
                    var inner = '<div class="d-flex align-items-center gap-2">' +
                                '<i class="bi bi-collection text-primary"></i>' +
                                '<strong class="text-main" style="font-size: 0.85rem;"></strong>';
                    if (suite) {
                        inner += '<span class="text-muted small"></span>';
                    }
                    inner += '</div>';
                    td.innerHTML = inner;
                    td.querySelector('strong').textContent = pretty;
                    var tail = td.querySelector('span');
                    if (tail && suite) {
                        tail.textContent = '— ' + suite;
                        tail.title = suite;
                    }
                    header.appendChild(td);
                    row.parentNode.insertBefore(header, row);
                }
            }

            // Wait for DataTables to initialize on this table, then attach the draw handler.
            // DataTables auto-init runs on $(document).ready in site.js, which may fire after
            // this code. Poll briefly until ready (no-op once attached).
            function attach() {
                if (!isDtReady()) return false;
                jQuery(table).on('draw.dt', render);
                render();
                return true;
            }
            if (!attach() && window.jQuery) {
                var attempts = 0;
                var poll = setInterval(function () {
                    attempts++;
                    if (attach() || attempts > 60) clearInterval(poll);
                }, 50);
            }
        }

        function hydrateLatestRun() {
            var latestRunScript = document.getElementById('latestAssessmentRun');
            if (!latestRunScript || !latestRunScript.textContent.trim()) {
                return;
            }

            try {
                var latestRun = JSON.parse(latestRunScript.textContent);
                if (!latestRun || !Array.isArray(latestRun.results)) {
                    return;
                }

                updateSummary(latestRun);
                updateRows(latestRun.results, { updateStats: false });
            } catch (error) {
                if (summary) {
                    summary.textContent = 'Could not restore the latest assessment run.';
                }
            }
        }

        function showDialog(icon, title, htmlContent, confirmText, size) {
            var iconMap = {
                info:    { cls: 'bi-info-circle',         color: 'text-primary' },
                success: { cls: 'bi-check-circle',        color: 'text-success' },
                warning: { cls: 'bi-exclamation-triangle', color: 'text-warning' },
                error:   { cls: 'bi-x-circle',            color: 'text-danger'  },
                question:{ cls: 'bi-question-circle',     color: 'text-info'    }
            };
            var iconCfg = iconMap[icon] || iconMap.info;
            var sizeClass = size === 'xl' ? ' modal-xl' : size === 'lg' ? ' modal-lg' : size === 'sm' ? ' modal-sm' : '';

            if (!window.bootstrap || !window.bootstrap.Modal) {
                window.alert(title + '\n\n' + (htmlContent || '').replace(/<[^>]+>/g, ''));
                return;
            }

            var modalEl = document.createElement('div');
            modalEl.className = 'modal fade';
            modalEl.tabIndex = -1;
            modalEl.setAttribute('aria-hidden', 'true');
            modalEl.innerHTML =
                '<div class="modal-dialog modal-dialog-centered modal-dialog-scrollable' + sizeClass + '">' +
                  '<div class="modal-content">' +
                    '<div class="modal-header">' +
                      '<h5 class="modal-title d-flex align-items-center">' +
                        '<i class="bi ' + iconCfg.cls + ' ' + iconCfg.color + ' me-2"></i>' +
                        '<span class="dialog-title"></span>' +
                      '</h5>' +
                      '<button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>' +
                    '</div>' +
                    '<div class="modal-body"></div>' +
                    '<div class="modal-footer">' +
                      '<button type="button" class="btn btn-primary dialog-confirm" data-bs-dismiss="modal"></button>' +
                    '</div>' +
                  '</div>' +
                '</div>';

            modalEl.querySelector('.dialog-title').textContent = title;
            modalEl.querySelector('.modal-body').innerHTML = htmlContent || '';
            modalEl.querySelector('.dialog-confirm').textContent = confirmText || 'OK';

            document.body.appendChild(modalEl);
            var bsModal = new window.bootstrap.Modal(modalEl);
            modalEl.addEventListener('hidden.bs.modal', function () {
                modalEl.remove();
            });
            bsModal.show();
        }

        function escapeHtml(value) {
            return String(value || '')
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#39;');
        }

        // ── Update Run Selected Button Visibility ───────────────
        function updateRunSelectedButton() {
            const selectedCount = document.querySelectorAll('.case-selector:checked').length;
            if (runSelectedButton) {
                runSelectedButton.classList.toggle('d-none', selectedCount === 0);
                const span = runSelectedButton.querySelector('span');
                if (span) {
                    span.textContent = 'Run Selected (' + selectedCount + ')';
                }
            }
        }
    });
})();
