/**
 * Investigation → Pipeline View tab
 *
 * Renders the canonical pipeline workflow as a Mermaid flowchart, then wires click handlers
 * onto every node so the user can inspect step-level detail (status, timing, SQL, sub-steps,
 * input/output, errors) in the card directly below the graph.
 *
 * The graph definition + the node→steps map are server-rendered into the page by
 * _InvestigationPipelineGraph.cshtml — this script only reads them, runs Mermaid, and binds
 * clicks. There is no API call.
 */

(function () {
    'use strict';

    let nodeMap = {};        // { "CLASS": [{step}, ...], ... }
    let nodeNames = {};      // { "CLASS": "Classifier (Intent + Plan)", ... }
    let nodeOrder = {};      // { "CLASS": 4, ... } — first-encounter step index per active node
    let activeEdges = {};    // { "Q->GUARD": true, ... } — edges on the actual journey
    let pathNodes = {};      // { "GUARD": true, ... } — every node on the journey (incl. passthroughs)
    let passthroughNodes = {}; // { "GUARD": true, ... } — on path but didn't emit a step ("transparent passthrough")

    // ─── Step-job descriptions ─────────────────────────────────────────────────────────
    // Short plain-English explanation of WHAT EACH STEP DOES — surfaced in the detail card
    // under the step header so the user understands the purpose of every stage they click.
    // Matched by exact lower-cased action name first, then by a fallback substring match (so
    // "LLM call (Planner)" / "LLM call (Explainer)" both hit the same "llm call" entry).
    // Keep these tight: one sentence describing the job, optionally a second on what it
    // returns or how it decides. If you add a new orchestrator stage, add an entry here too —
    // otherwise the detail card will fall back to the generic "Pipeline stage" placeholder.
    const STEP_DESCRIPTIONS = {
        // Top-level pipeline stages
        'preflight':              'Pre-LLM regex check on the raw question. Refuses write intents ("delete tickets"), out-of-scope topics (predictions, opinions), and secret-disclosure attempts before any expensive call.',
        'operationalguard':       'Kill-switch and rate-limit gate. Cheapest possible refusal — no LLM, no DB. Refuses when an admin disabled the copilot or the conversation is sending requests too fast.',
        'decomposer':             'Splits compound questions ("X and Y", "X versus Y") into independent sub-questions, runs each through the pipeline, then stitches the answers back together.',
        'conversational':         'Catches greetings, thanks, farewells, "what can you do" — answers with a templated reply without touching the LLM or the DB.',
        'knowledgematch':         'Catches glossary-style questions ("what is a ticket?", "explain priority levels"). Returns the entity / metric / dimension description from the semantic layer.',
        'semanticsearch':         'Catches "tickets similar to X" / "find tickets about Y". Uses pre-computed vector embeddings to rank matches — bypasses the SQL planner entirely.',
        'metadatahandler':        'Catches schema questions ("what tables exist?", "what columns are in Tickets?"). Runs INFORMATION_SCHEMA queries, filtered to hide sensitive columns and optionally restricted to configured entities.',
        'tooldispatch':           'Scores the question against registered external tools (weather, currency, country profile, etc.). On a strong match, dispatches an HTTP call and projects the JSON response into a tabular result.',
        'shapeengine':            'Tier-2 deterministic planner. Tries 8 question shapes (CountBy / AggregateBy / TopNBy / HavingCount / OrderedBy / TemporalScope / Distinct / NaturalKeyLookup). On a Full-confidence match it builds the QuerySpec WITHOUT an LLM call.',
        'retriever':              'Picks the most relevant tables for the question. Vector-similarity (default) or keyword overlap, trimmed to top-K plus FK neighbours. Builds the schema prompt the planner sees.',
        'planner':                'Calls the LLM with the question + schema slice. Produces a structured QuerySpec JSON: intent, root entity, select columns, aggregations, filters, group-by, order-by, limit, joins.',
        'planner-retry':          'Self-corrector retry of the planner with the previous attempt\'s SQL error as context. Capped at MaxSelfCorrectionRetries (default 1) so a bad question can\'t loop forever.',
        'retry':                  'Self-corrector hop back to the planner: the previous compiled SQL hit an error, so we feed that error in as a hint and ask the LLM to revise its plan.',
        'intentnormalizer':       'Post-LLM safety net: re-classifies "out-of-scope" to "data_query" when the question clearly names an entity, and fills empty Root from the best synonym match.',
        'entityrootguard':        'Overrides the planner\'s root table when the user clearly named a different entity ("how many cases ..." → root=Tickets via the "case" synonym).',
        'literaldateguard':       'Forces literal ISO dates from the question back into the spec when the LLM substituted a derived @-token. Also routes date verbs ("closed", "resolved") to the matching column via the semantic layer\'s date roles.',
        'intentcoercer':          'Rewrites COUNT-shaped specs to row-list specs when the question said "show me" / "list" / "find" but the LLM defaulted to COUNT.',
        'intentcoercer-antijoin': 'Enforces an anti-join (LEFT JOIN + IS NULL) when the question is "X with no Y" / "X without any Y" / "X missing a Y".',
        'intentrouter':           'Decides what to do with the planner\'s intent: data_query → SQL execution; clarification → ask a follow-up; out_of_scope / metadata → polite refusal.',
        'clarificationgate':      'Returns the planner\'s clarification question as the reply when the planner couldn\'t unambiguously map the question to a spec.',
        'compiler':               'Translates the QuerySpec into a parameterised SQL Server query. Auto-injects soft-delete filters, joins lookup tables, qualifies columns, expands metrics / dimensions from the semantic layer.',
        'validator':              'Parses the compiled SQL with the T-SQL ScriptDom AST. Refuses anything that isn\'t a SELECT, blocks INFORMATION_SCHEMA reads, prevents projection of sensitive columns.',
        'resultshapevalidator':   'Post-execute shape check: aggregate queries get ≥1 row with ≥2 cols, top-N returns ≤N rows, scalar queries are 1×1. Tags a "shape-mismatch" trace token on violations.',
        'executor':               'Runs the validated SQL against the database with row caps and command timeouts. Wraps in PII redaction, cost-gate, and result-cache layers.',
        'retrydegenerationguard': 'Refuses retry attempts that dropped the aggregations the user asked for. "Average X" should never silently turn into a row list — surfaces the original error instead.',
        'explainer':              'Generates the final answer. Either templated (markdown table) or LLM-driven (1-2 sentence summary + data table + table citations).',
        'conversationcontext':    'Builds the prior-turn context block injected into the planner prompt for multi-turn refinements ("sort by date", "now critical only").',
        'tier':                   'Informational marker stamping which tier won this request (Tier-1 deterministic / Tier-2 shape engine / Tier-3 LLM planner). Does no work itself.',

        // Graph-node aliases — keyed on the short node IDs used in the Mermaid graph so the
        // detail card finds a description when the user clicks a node that didn't emit its
        // own step (passthroughs / branches not taken). The full names already match via the
        // entries above; these are the short codes used in the SVG.
        'q':         'The user\'s original question. Start of every pipeline run; carries the raw text plus any conversation context (history, suite metadata).',
        'guard':     'Kill-switch and rate-limit gate. Cheapest possible refusal — no LLM, no DB. Refuses when an admin disabled the copilot or the conversation is sending requests too fast.',
        'intake':    'Pre-LLM regex check on the raw question. Refuses write intents ("delete tickets"), out-of-scope topics, secret-disclosure attempts before any expensive call.',
        'decompose': 'Splits compound questions ("X and Y", "X versus Y") into independent sub-questions, runs each through the pipeline, then stitches the answers back together.',
        'conv':      'Catches greetings, thanks, farewells, "what can you do" — answers with a templated reply without touching the LLM or the DB.',
        'known':     'Catches glossary-style questions ("what is a ticket?", "explain priority levels"). Returns the entity / metric / dimension description from the semantic layer.',
        'semsearch': 'Catches "tickets similar to X" / "find tickets about Y". Uses pre-computed vector embeddings to rank matches — bypasses the SQL planner entirely.',
        'metadata':  'Catches schema questions ("what tables exist?", "what columns are in Tickets?"). Runs INFORMATION_SCHEMA queries, filtered to hide sensitive columns and optionally restricted to configured entities.',
        'tool':      'Scores the question against registered external tools (weather, currency, country profile, etc.). On a strong match, dispatches an HTTP call and projects the JSON response into a tabular result.',
        'shape':     'Tier-2 deterministic planner. Tries 8 question shapes (CountBy / AggregateBy / TopNBy / HavingCount / OrderedBy / TemporalScope / Distinct / NaturalKeyLookup). On a Full-confidence match it builds the QuerySpec WITHOUT an LLM call.',
        'retr':      'Picks the most relevant tables for the question. Vector-similarity (default) or keyword overlap, trimmed to top-K plus FK neighbours. Builds the schema prompt the planner sees.',
        'plan':      'Calls the LLM with the question + schema slice. Produces a structured QuerySpec JSON: intent, root entity, select columns, aggregations, filters, group-by, order-by, limit, joins.',
        'guards':    'Post-planner deterministic guards (IntentNormalizer / EntityRootGuard / LiteralDateGuard / IntentCoercer / IntentCoercer-AntiJoin) that fix common LLM mistakes before compilation.',
        'route':     'Decides what to do with the planner\'s intent: data_query → SQL execution; clarification → ask a follow-up; out_of_scope / metadata → polite refusal.',
        'ctrl':      'Clarification gate — returns the planner\'s clarification question as the reply when the planner couldn\'t unambiguously map the user\'s question to a spec.',
        'comp':      'Translates the QuerySpec into a parameterised SQL Server query. Auto-injects soft-delete filters, joins lookup tables, qualifies columns, expands metrics / dimensions from the semantic layer.',
        'val':       'Parses the compiled SQL with the T-SQL ScriptDom AST. Refuses anything that isn\'t a SELECT, blocks INFORMATION_SCHEMA reads, prevents projection of sensitive columns.',
        'exec':      'Runs the validated SQL against the database with row caps and command timeouts. Wraps in PII redaction, cost-gate, and result-cache layers.',
        'degen':     'Refuses retry attempts that dropped the aggregations the user asked for. "Average X" should never silently turn into a row list — surfaces the original error instead.',
        'fmt':       'Generates the final answer. Either templated (markdown table) or LLM-driven (1-2 sentence summary + data table + table citations).',
        'abort':     'Pipeline terminated with an error (preflight refusal, planner exhaustion, compiler error, validator rejection, executor failure, retry degeneration, kill switch, rate limit, etc.). Carries the failure reason.',
        'ans':       'Final answer delivered to the user. End of every successful pipeline run.',

        // Sub-step descriptions — matched after exact lookup fails. Most are recognisable
        // by a substring fragment in the action name (case-insensitive).
        'sub-question':           'One independent sub-question split off by the Decomposer. Each runs through the full pipeline; results are merged at the end.',
        'prompt assembly':        'Builds the system + user prompts sent to the LLM. Includes the semantic-layer summary, few-shot examples, and learning-RAG hits from past successful traces.',
        'llm call':               'Single round-trip to the LLM. The TechnicalData panel shows the exact prompt that was sent and the raw response that came back.',
        'json parse':             'Validates and deserialises the LLM\'s JSON response into a strongly-typed QuerySpec object.',
        'tool resolution':        'Scores every registered tool against the question by keyword overlap; picks the top match when it clears the confidence threshold, or returns "no tool" otherwise.',
        'compose final reply':    'Combines the LLM summary, the data table, and citations into the final markdown reply shown to the user.',
        'template render':        'Templated explainer fallback — emits a "Returned N row(s)" header plus a markdown table of the result rows. Used when the LLM explainer is disabled or trivially short-circuited.',
        'tool dispatch':          'HTTP GET to the resolved tool\'s endpoint, with placeholders substituted from the question. Returns the raw response body plus status code.',
        'tool format':            'Projects the tool\'s JSON / text response into rows + columns so the explainer can render it like any other tabular result.',
        'metadata cache':         'Reads / writes the trace metadata cache so repeat questions can short-circuit through the result-cache layer.',
    };

    /**
     * Look up the "what does this step do" description for an action / node name. Tries:
     *   1. Exact lower-cased match (matches StageNames action constants like "compiler").
     *   2. Space-stripped match so friendly node names like "Operational Guard" → "operationalguard".
     *   3. Substring scan over description-map keys so variants like "LLM call (Planner)"
     *      still hit the "llm call" entry. Longer keys first so more-specific matches win.
     * Returns null when nothing matches — caller renders no description block.
     */
    function describeStep(action) {
        if (!action) return null;
        const key = action.toLowerCase().trim();
        if (STEP_DESCRIPTIONS[key]) return STEP_DESCRIPTIONS[key];
        const compact = key.replace(/\s+/g, '');
        if (STEP_DESCRIPTIONS[compact]) return STEP_DESCRIPTIONS[compact];
        const keys = Object.keys(STEP_DESCRIPTIONS).sort((a, b) => b.length - a.length);
        for (const k of keys) {
            if (key.indexOf(k) !== -1 || compact.indexOf(k) !== -1) return STEP_DESCRIPTIONS[k];
        }
        return null;
    }

    // Pan/zoom state — module-scoped so it persists across re-renders (e.g. when the user
    // toggles layout direction we re-run Mermaid but want to keep the same handlers bound).
    let pzScale = 1;
    let pzPanX = 0;
    let pzPanY = 0;
    let pzDirection = 'LR';
    let pzHandlersBound = false;
    let pzOriginalSrc = ''; // raw Mermaid source captured before run() replaces it with SVG
    // Localized strings server-emitted in #pipeline-i18n. Default English fallbacks let the
    // module still render usefully if the page is missing the i18n island for any reason.
    let i18n = {
        notTakenBadge: 'Not taken',
        notInActivePath: 'This branch was not part of the active path for this request. It exists in the pipeline so other questions can route through it.',
        stepsRecorded: 'step(s) recorded',
        stepIndexOf: 'Step {0} of {1}',
        layerLabel: 'Layer',
        startedLabel: 'Started',
        completedLabel: 'Completed',
        componentLabel: 'Component',
        summaryLabel: 'Summary',
        substepsLabel: 'Sub-steps',
        technicalData: 'Technical data',
        promptSent: 'Prompt sent to model',
        rawResponse: 'Raw response from model',
        sqlStatement: 'SQL statement',
        inputLabel: 'Input',
        outputLabel: 'Output',
        copy: 'Copy',
        copied: 'Copied',
        providerError: 'Provider error',
        databaseError: 'Database error',
        executionFailed: 'Execution failed',
        mermaidFailed: 'Mermaid library failed to load.',
        renderFailed: 'Failed to render the pipeline graph:'
    };

    function format(template, ...args) {
        return String(template).replace(/\{(\d+)\}/g, (_, idx) => args[Number(idx)] ?? '');
    }

    function init() {
        const graphEl = document.getElementById('pipeline-mermaid-graph');
        if (!graphEl) return; // not on this page

        // Capture the raw Mermaid source NOW — mermaid.run() will replace textContent with an
        // <svg>, after which we can't recover the original. We need this to support direction
        // toggling (LR ⟷ TB) without a page reload.
        pzOriginalSrc = graphEl.textContent || '';

        // Pull the inline JSON payloads. They're inside <script type="application/json"> blocks
        // so the browser doesn't try to execute them — JSON.parse() handles escaping safely.
        try {
            const mapEl = document.getElementById('pipeline-node-map');
            const namesEl = document.getElementById('pipeline-node-names');
            const orderEl = document.getElementById('pipeline-node-order');
            const edgesEl = document.getElementById('pipeline-active-edges');
            const pathEl = document.getElementById('pipeline-path-nodes');
            const passEl = document.getElementById('pipeline-passthrough-nodes');
            const i18nEl = document.getElementById('pipeline-i18n');
            nodeMap = mapEl ? JSON.parse(mapEl.textContent || '{}') : {};
            nodeNames = namesEl ? JSON.parse(namesEl.textContent || '{}') : {};
            nodeOrder = orderEl ? JSON.parse(orderEl.textContent || '{}') : {};
            activeEdges = edgesEl ? JSON.parse(edgesEl.textContent || '{}') : {};
            pathNodes = pathEl ? JSON.parse(pathEl.textContent || '{}') : {};
            passthroughNodes = passEl ? JSON.parse(passEl.textContent || '{}') : {};
            if (i18nEl) {
                Object.assign(i18n, JSON.parse(i18nEl.textContent || '{}'));
            }
        } catch (err) {
            console.error('Pipeline: failed to parse inline payload', err);
        }

        if (typeof window.mermaid === 'undefined') {
            graphEl.innerHTML = '<div class="alert alert-warning small">' + escapeHtml(i18n.mermaidFailed) + '</div>';
            return;
        }

        window.mermaid.initialize({
            startOnLoad: false,
            theme: 'default',
            // Bump fontSize so node labels stay legible when the user zooms out of a wide LR
            // graph. The previous default (~14px) reduced to ~7px at zoom 0.5 — illegible.
            // 16px reduces to ~8px at zoom 0.5 and stays crisp at zoom 1.5+.
            fontSize: 16,
            flowchart: { htmlLabels: true, curve: 'basis', useMaxWidth: true, nodeSpacing: 50, rankSpacing: 60 },
            securityLevel: 'loose' // needed so click bindings work on rendered nodes
        });

        // The Pipeline View tab might be hidden when the page first loads (Story is the default tab).
        // Mermaid measures node sizes from the DOM — if the container is display:none, the layout
        // collapses to zero. Render now if the tab is visible; otherwise wait until it shows.
        const tab = document.getElementById('view-tab');
        if (tab && tab.classList.contains('active')) {
            renderGraph();
        }

        // Re-render whenever the user switches into the Pipeline View tab. The Bootstrap event
        // 'shown.bs.tab' fires after the pane is visible and measurable.
        document.querySelectorAll('a[href="?tab=view"], [data-bs-toggle="tab"][href="#view-tab"]').forEach(trigger => {
            trigger.addEventListener('shown.bs.tab', renderGraph);
        });

        // Fallback: if the tab is the active one but Mermaid hasn't rendered yet (the script
        // loaded after the DOM was painted), give it one render pass on the next animation frame.
        if (tab && tab.classList.contains('active')) {
            requestAnimationFrame(renderGraph);
        }
    }

    let rendered = false;
    async function renderGraph() {
        if (rendered) return; // Mermaid is idempotent but re-running mutates IDs and breaks bindings
        const graphEl = document.getElementById('pipeline-mermaid-graph');
        if (!graphEl) return;

        try {
            // Mermaid v10 API: run() processes all .mermaid blocks on the page.
            await window.mermaid.run({ nodes: [graphEl] });
            rendered = true;
            wireClickHandlers(graphEl);
            // Pan / zoom / direction / active-only toggles. Initialised AFTER Mermaid has
            // painted the SVG so we can attach handlers to the real DOM, not the placeholder.
            initPanZoomAndToolbar();
        } catch (err) {
            console.error('Pipeline: Mermaid render failed', err);
            graphEl.innerHTML = '<div class="alert alert-danger small">' + escapeHtml(i18n.renderFailed) + ' ' + escapeHtml(err.message || String(err)) + '</div>';
        }
    }

    /**
     * Bind click handlers onto each node in the rendered SVG. Mermaid assigns each node an id
     * like "flowchart-CLASS-12" and the user-supplied id ("CLASS") is the second segment, so
     * we extract that and look it up in our map.
     *
     * Also tags nodes that are part of the active path (or failed) so the CSS pulse picks them
     * up — the active set comes from pathNodes, which is the server-side expansion of nodeOrder
     * by active-edge endpoints (every node ON the journey, whether or not it emitted its own
     * step). Skipped-status stages are still excluded so the colouring matches the real path.
     */
    function wireClickHandlers(graphEl) {
        const activeIds = new Set(Object.keys(pathNodes || {}).map(k => k.toUpperCase()));

        const nodes = graphEl.querySelectorAll('g.node');
        nodes.forEach(node => {
            const rawId = node.id || '';
            const parts = rawId.split('-'); // ["flowchart", "CLASS", "12"]
            const userId = parts.length >= 2 ? parts[1] : null;
            if (!userId) return;

            const isActive = activeIds.has(userId.toUpperCase());
            const hasError = isActive && (nodeMap[userId] || []).some(s => (s.status || '').toLowerCase() === 'error');

            if (hasError) {
                node.classList.add('pipeline-failed');
            } else if (isActive) {
                node.classList.add('pipeline-active');
            }

            node.addEventListener('click', () => showNodeDetail(userId));
        });
    }

    /**
     * Populate the detail card with everything we know about the clicked node.
     * Renders one accordion section per recorded step (some nodes have multiple — e.g. CLASS
     * has the unified-analysis step plus an aborted-step in error cases).
     */
    function showNodeDetail(nodeId) {
        const card = document.getElementById('pipeline-detail-card');
        if (!card) return;

        const friendlyName = nodeNames[nodeId] || nodeId;
        const steps = nodeMap[nodeId] || [];

        if (steps.length === 0) {
            // Three cases for "no recorded step":
            //   1. Passthrough — node IS on the active journey (edge BFS includes it) but the
            //      orchestrator didn't emit a step because the stage did no meaningful work
            //      (Operational Guard on success, Request Intake on success, Tier-1 cascade
            //      fall-through). Show a friendly "passed through silently" badge + sentence.
            //   2. Branch not taken — node exists in the graph but the request never reached it
            //      (Clarification Guard on a non-ambiguous query, Pipeline Aborted on success).
            //      Original "not in active path" wording.
            //   3. Stage description from STEP_DESCRIPTIONS — always shown so the user learns
            //      what the stage does regardless of whether it fired this run.
            const upperId = (nodeId || '').toUpperCase();
            const isPassthrough = passthroughNodes[nodeId] || passthroughNodes[upperId];
            const desc = describeStep(nodeId) || describeStep(friendlyName);
            const badge = isPassthrough
                ? `<span class="badge bg-soft-info text-info rounded-pill">
                       <i class="bi bi-arrow-right-circle me-1"></i>Passed through (no step)
                   </span>`
                : `<span class="badge bg-soft-secondary text-secondary rounded-pill">
                       <i class="bi bi-dash-circle me-1"></i>${escapeHtml(i18n.notTakenBadge)}
                   </span>`;
            const explanation = isPassthrough
                ? 'The request was on this stage in the pipeline, but it did no meaningful work here — so the orchestrator didn\'t record its own step. This is normal for stages that are gates / cascades / transparent passthroughs (e.g. Operational Guard on a healthy request, the Tier-1 fall-through chain when no handler matched).'
                : i18n.notInActivePath;
            const descBlock = desc
                ? `<div class="mt-3 pipeline-step-job">
                       <div class="small fw-bold text-uppercase text-info ls-1 mb-1">
                           <i class="bi bi-info-circle me-1"></i>What this step does
                       </div>
                       <div class="small pipeline-step-job-text">${escapeHtml(desc)}</div>
                   </div>`
                : '';
            card.innerHTML = `
                <div class="card-body p-4">
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <h5 class="fw-bold mb-0">${escapeHtml(friendlyName)}</h5>
                        ${badge}
                    </div>
                    <p class="text-muted small mb-0">${escapeHtml(explanation)}</p>
                    ${descBlock}
                </div>`;
            scrollToCard(card);
            return;
        }

        const html = `
            <div class="card-body p-4">
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h5 class="fw-bold mb-0"><i class="bi bi-diagram-3 me-2"></i>${escapeHtml(friendlyName)}</h5>
                    <span class="badge bg-soft-primary text-primary rounded-pill">${steps.length} ${escapeHtml(i18n.stepsRecorded)}</span>
                </div>
                ${steps.map((s, i) => renderStepBlock(s, i, steps.length)).join('')}
            </div>`;
        card.innerHTML = html;
        scrollToCard(card);
    }

    /**
     * Render a single step (top-level OR sub-step) and recurse into its own subSteps.
     * Everything is shown expanded by default — no collapse / accordion. The user explicitly
     * wants to see all detail at once when they click a node.
     *
     * `depth` lets us indent nested levels visually so the tree shape is obvious even when
     * a parent has many children.
     */
    function renderStepBlock(step, index, total, depth = 0) {
        const statusBadge = renderStatusBadge(step.status);
        const componentMatch = (step.action || '').match(/\[([^\]]+)\]/);
        const component = componentMatch ? componentMatch[1] : '';
        const cleanAction = (step.action || '').replace(/\s*\[[^\]]+\]/g, '').trim() || 'Step';

        const typed = parseTypedPayload(step.technicalData);

        const indentClass = depth === 0 ? 'pipeline-step-root' : 'pipeline-step-nested';
        const wrapperStyle = depth === 0
            ? 'background: var(--bg-secondary, #f8f9fa);'
            : 'background: var(--bg-primary, #fff); border-left: 3px solid var(--border-color, #dee2e6);';

        return `
            <div class="border rounded-3 p-3 mb-3 ${indentClass}" style="${wrapperStyle} margin-left: ${depth * 12}px;">
                <div class="d-flex flex-wrap justify-content-between align-items-center mb-2 gap-2">
                    <div class="d-flex flex-wrap align-items-center gap-2">
                        ${total > 1 && depth === 0 ? `<span class="badge bg-soft-secondary text-secondary rounded-pill small">${escapeHtml(format(i18n.stepIndexOf, index + 1, total))}</span>` : ''}
                        ${statusBadge}
                        <span class="fw-semibold small">${escapeHtml(cleanAction)}</span>
                        ${component ? `<code class="small text-muted">${escapeHtml(component)}</code>` : ''}
                    </div>
                    <span class="small text-muted">
                        <i class="bi bi-stopwatch me-1"></i>${formatMs(step.elapsedMs)}
                    </span>
                </div>

                <div class="row small g-2 mb-2 text-muted">
                    <div class="col-md-3"><strong>${escapeHtml(i18n.layerLabel)}:</strong> ${escapeHtml(step.layer || '—')}</div>
                    <div class="col-md-3"><strong>${escapeHtml(i18n.startedLabel)}:</strong> ${escapeHtml(step.startedAt || '—')}</div>
                    <div class="col-md-3"><strong>${escapeHtml(i18n.completedLabel)}:</strong> ${escapeHtml(step.completedAt || '—')}</div>
                    <div class="col-md-3"><strong>${escapeHtml(i18n.componentLabel)}:</strong> ${escapeHtml(step.location || component || '—')}</div>
                </div>

                ${renderStepJobDescription(cleanAction)}

                ${step.detail ? `
                    <div class="mb-2">
                        <div class="small fw-bold text-uppercase text-muted ls-1 mb-1">${escapeHtml(i18n.summaryLabel)}</div>
                        <pre class="small p-2 rounded mb-0 pipeline-pre-soft">${escapeHtml(step.detail)}</pre>
                    </div>` : ''}

                ${renderTypedPayload(typed, step.technicalData)}

                ${step.subSteps && step.subSteps.length > 0 ? `
                    <div class="mt-3">
                        <div class="small fw-bold text-uppercase text-primary ls-1 mb-2">
                            <i class="bi bi-diagram-2 me-1"></i>${escapeHtml(i18n.substepsLabel)} (${step.subSteps.length})
                        </div>
                        ${step.subSteps.map((ss, i) => renderStepBlock(ss, i, step.subSteps.length, depth + 1)).join('')}
                    </div>` : ''}
            </div>`;
    }

    /**
     * Render the "what this step does" explainer box. Surfaced under the meta-row (layer /
     * started / completed / component) so the user immediately sees the stage's PURPOSE
     * before drilling into its summary + technical data. Falls back silently when the action
     * has no description (e.g. legacy steps that pre-date the STEP_DESCRIPTIONS map) — better
     * to show no panel than a placeholder that adds noise.
     */
    function renderStepJobDescription(cleanAction) {
        const desc = describeStep(cleanAction);
        if (!desc) return '';
        return `
            <div class="mb-2 pipeline-step-job">
                <div class="small fw-bold text-uppercase text-info ls-1 mb-1">
                    <i class="bi bi-info-circle me-1"></i>What this step does
                </div>
                <div class="small pipeline-step-job-text">${escapeHtml(desc)}</div>
            </div>`;
    }

    /**
     * Try to parse the TechnicalData blob as one of our typed payloads. Returns null when the
     * blob isn't typed JSON — caller falls back to rendering it as raw text.
     */
    function parseTypedPayload(blob) {
        if (!blob || typeof blob !== 'string') return null;
        const trimmed = blob.trim();
        if (!trimmed.startsWith('{')) return null;
        try {
            const obj = JSON.parse(trimmed);
            if (obj && typeof obj === 'object' && typeof obj.kind === 'string') return obj;
        } catch { /* not JSON, fine */ }
        return null;
    }

    /**
     * Render the typed-payload sections. Three shapes recognised:
     *   kind = "llm-call"      → Prompt + Response panels (with Copy buttons)
     *   kind = "sql-execution" → SQL panel + row/column counts
     *   kind = "function-call" → Input + Output panels
     *
     * For untyped TechnicalData blobs we just dump the raw text into a Technical Data panel.
     */
    function renderTypedPayload(typed, rawBlob) {
        if (!typed) {
            if (rawBlob && rawBlob.trim().length > 0) {
                return `
                    <div class="mb-2">
                        <div class="small fw-bold text-uppercase text-muted ls-1 mb-1">${escapeHtml(i18n.technicalData)}</div>
                        <pre class="small p-2 rounded mb-0 pipeline-pre-soft"><code>${escapeHtml(rawBlob)}</code></pre>
                    </div>`;
            }
            return '';
        }

        if (typed.kind === 'llm-call') {
            return `
                <div class="mb-2">
                    <div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                        <span class="badge bg-soft-primary text-primary rounded-pill"><i class="bi bi-cpu me-1"></i>${escapeHtml(typed.provider || '')} / ${escapeHtml(typed.model || '')}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill"><i class="bi bi-arrow-up-right me-1"></i>${typed.promptLength || 0}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill"><i class="bi bi-arrow-down-left me-1"></i>${typed.responseLength || 0}</span>
                        ${typed.providerSuccess === false ? `<span class="badge bg-soft-danger text-danger rounded-pill"><i class="bi bi-x-circle-fill me-1"></i>${escapeHtml(i18n.providerError)}</span>` : ''}
                    </div>
                    ${typed.providerError ? `
                        <div class="alert alert-danger small p-2 mb-2">
                            <strong>${escapeHtml(i18n.providerError)}:</strong> ${escapeHtml(typed.providerError)}
                        </div>` : ''}
                    <div class="mb-2">
                        ${renderPaneHeader(i18n.promptSent, 'bi-arrow-up-right')}
                        <pre class="small p-2 rounded mb-0 pipeline-pre-prompt">${escapeHtml(typed.prompt || '')}</pre>
                    </div>
                    <div class="mb-2">
                        ${renderPaneHeader(i18n.rawResponse, 'bi-arrow-down-left')}
                        <pre class="small p-2 rounded mb-0 pipeline-pre-response">${escapeHtml(typed.response || '')}</pre>
                    </div>
                </div>`;
        }

        if (typed.kind === 'sql-execution') {
            return `
                <div class="mb-2">
                    <div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                        <span class="badge bg-soft-primary text-primary rounded-pill"><i class="bi bi-database me-1"></i>${typed.rowCount || 0}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill"><i class="bi bi-grid-3x2 me-1"></i>${typed.columnCount || 0}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill"><i class="bi bi-stopwatch me-1"></i>${typed.elapsedMs || 0} ms</span>
                        ${typed.success === false ? `<span class="badge bg-soft-danger text-danger rounded-pill"><i class="bi bi-x-circle-fill me-1"></i>${escapeHtml(i18n.executionFailed)}</span>` : ''}
                    </div>
                    ${typed.errorMessage ? `
                        <div class="alert alert-danger small p-2 mb-2">
                            <strong>${escapeHtml(i18n.databaseError)}:</strong> ${escapeHtml(typed.errorMessage)}
                        </div>` : ''}
                    <div class="mb-2">
                        ${renderPaneHeader(i18n.sqlStatement, 'bi-code-square')}
                        <pre class="small p-2 rounded mb-0 pipeline-pre-sql"><code>${escapeHtml(typed.sql || '')}</code></pre>
                    </div>
                </div>`;
        }

        if (typed.kind === 'function-call') {
            return `
                <div class="mb-2">
                    <div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                        <span class="badge bg-soft-primary text-primary rounded-pill"><i class="bi bi-braces me-1"></i>${escapeHtml(typed.function || '')}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill">${escapeHtml(i18n.inputLabel)}: ${typed.inputLength || 0}</span>
                        <span class="badge bg-soft-secondary text-secondary rounded-pill">${escapeHtml(i18n.outputLabel)}: ${typed.outputLength || 0}</span>
                    </div>
                    ${typed.description ? `<div class="small text-muted mb-2">${escapeHtml(typed.description)}</div>` : ''}
                    <div class="mb-2">
                        ${renderPaneHeader(i18n.inputLabel, 'bi-arrow-right-circle')}
                        <pre class="small p-2 rounded mb-0 pipeline-pre-soft">${escapeHtml(typed.input || '')}</pre>
                    </div>
                    <div class="mb-2">
                        ${renderPaneHeader(i18n.outputLabel, 'bi-arrow-left-circle')}
                        <pre class="small p-2 rounded mb-0 pipeline-pre-soft">${escapeHtml(typed.output || '')}</pre>
                    </div>
                </div>`;
        }

        // Unknown typed payload — render the raw JSON so we don't lose information.
        return `
            <div class="mb-2">
                <div class="small fw-bold text-uppercase text-muted ls-1 mb-1">${escapeHtml(i18n.technicalData)} (${escapeHtml(typed.kind)})</div>
                <pre class="small p-2 rounded mb-0 pipeline-pre-soft"><code>${escapeHtml(JSON.stringify(typed, null, 2))}</code></pre>
            </div>`;
    }

    /**
     * Header row above each pre-formatted pane: label on the left, Copy button on the right.
     * The Copy button finds its sibling <pre> and copies its textContent.
     */
    function renderPaneHeader(label, icon) {
        const copyText = escapeHtml(i18n.copy);
        const copiedText = escapeHtml(i18n.copied);
        return `
            <div class="d-flex justify-content-between align-items-center mb-1">
                <div class="small fw-bold text-uppercase text-muted ls-1"><i class="bi ${icon} me-1"></i>${escapeHtml(label)}</div>
                <button type="button" class="btn btn-sm btn-link p-0" data-copy-label="${copyText}" data-copied-label="${copiedText}" onclick="(function(b){const p=b.parentElement.nextElementSibling;if(p&&p.textContent){navigator.clipboard.writeText(p.textContent);const copied=b.dataset.copiedLabel||'Copied';const copy=b.dataset.copyLabel||'Copy';b.textContent=copied;setTimeout(()=>b.textContent=copy,1200);}})(this)">${copyText}</button>
            </div>`;
    }

    function renderStatusBadge(status, iconOnly = false) {
        const map = {
            'Ok':    { cls: 'bg-soft-success text-success',    icon: 'bi-check-circle-fill' },
            'Warn':  { cls: 'bg-soft-warning text-warning',    icon: 'bi-exclamation-triangle-fill' },
            'Error': { cls: 'bg-soft-danger text-danger',      icon: 'bi-x-circle-fill' },
            'Skip':  { cls: 'bg-soft-secondary text-secondary', icon: 'bi-dash-circle' }
        };
        const m = map[status] || map['Ok'];
        return iconOnly
            ? `<span class="badge ${m.cls} rounded-pill"><i class="bi ${m.icon}"></i></span>`
            : `<span class="badge ${m.cls} rounded-pill"><i class="bi ${m.icon} me-1"></i>${status}</span>`;
    }

    function formatMs(ms) {
        const n = Number(ms) || 0;
        return n < 1000 ? `${n} ms` : `${(n / 1000).toFixed(2)} s`;
    }

    function escapeHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, ch => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[ch]));
    }

    function scrollToCard(card) {
        // Smooth-scroll the detail card into view so the user sees the result of their click
        // even if the graph is taller than the viewport.
        card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    // ---------------------------------------------------------------------------------------
    // Pan / zoom / view-mode toolbar.
    //
    // Mermaid's default rendering with `useMaxWidth: true` keeps the SVG at its natural width
    // when the viewport is narrower — meaning a wide LR (left-right) graph overflows
    // horizontally. To make the graph navigable we layer a CSS-transform-based pan & zoom on
    // top of the SVG, with toolbar buttons for the common operations.
    //
    // Implementation notes:
    //   - Transform = `translate(panX, panY) scale(scale)` on the .mermaid container.
    //   - Mouse wheel: cursor-anchored zoom (point under cursor stays put).
    //   - Mousedown + drag on background: pan. We skip drag-start on `g.node` so node clicks
    //     still fire normally.
    //   - "Active only" toggle: dims nodes that weren't part of the request's actual route.
    //     We pre-tag those nodes with class `inactive` so a simple CSS rule can fade them.
    //   - "LR / TB" toggle: re-renders Mermaid with the direction token swapped. State is
    //     preserved so handlers don't get re-bound.
    // ---------------------------------------------------------------------------------------

    function pzApplyTransform() {
        const graphEl = document.getElementById('pipeline-mermaid-graph');
        if (graphEl) {
            graphEl.style.transform = `translate(${pzPanX}px, ${pzPanY}px) scale(${pzScale})`;
        }
        const label = document.getElementById('pipeline-zoom-label');
        if (label) label.textContent = `${Math.round(pzScale * 100)}%`;
    }

    function pzClamp(s) { return Math.min(3.0, Math.max(0.3, s)); }

    /**
     * Stamp a small numbered badge ("step N") on an active node so the user can read the
     * actual execution sequence regardless of where the node sits in the layout. Mermaid
     * draws the node's shape inside g.node — we read its bounding box and append our badge
     * as a sibling g element so it doesn't get re-styled when Mermaid re-applies classes.
     * Order 0 is reserved for Q (the user-question terminal) and rendered as a "0" pill;
     * positive orders are the 1-based sequence of stages that actually ran.
     */
    function appendStepBadge(nodeEl, order) {
        const SVG_NS = 'http://www.w3.org/2000/svg';
        // Pick the visible shape (rect/path/polygon/circle) — Mermaid varies per node type.
        const shape = nodeEl.querySelector('rect, path, polygon, circle, ellipse');
        if (!shape) return;
        const box = shape.getBBox();
        // Anchor the badge to the top-right corner of the shape, nudged outward 4px so it
        // sits clear of the stroke and any drop-shadow filter Mermaid applies.
        const cx = box.x + box.width - 4;
        const cy = box.y + 4;
        const g = document.createElementNS(SVG_NS, 'g');
        g.setAttribute('class', 'pipeline-step-badge');
        g.setAttribute('transform', `translate(${cx}, ${cy})`);
        const circle = document.createElementNS(SVG_NS, 'circle');
        circle.setAttribute('r', '11');
        circle.setAttribute('class', 'pipeline-step-badge-bg');
        const text = document.createElementNS(SVG_NS, 'text');
        text.setAttribute('text-anchor', 'middle');
        text.setAttribute('dominant-baseline', 'central');
        text.setAttribute('class', 'pipeline-step-badge-text');
        text.textContent = String(order);
        g.appendChild(circle);
        g.appendChild(text);
        nodeEl.appendChild(g);
    }

    function pzTagInactive(graphEl) {
        // Two distinct sets:
        //   • pathIds — every node ON the journey (server-side expansion of nodeOrder by
        //     active-edge endpoints). A node here gets the dark "active" colour because the
        //     request demonstrably traversed it, even when it didn't emit its own step.
        //   • orderedIds — only nodes that emitted a step. Gets the numbered step-badge.
        // Without this split, Operational Guard / Request Intake / cascade-fallthrough stages
        // would render light blue despite being on the path — the user's "why some step have
        // dark color and some not" complaint.
        const pathIds = new Set(Object.keys(pathNodes || {}).map(k => k.toUpperCase()));
        const orderedIds = new Set(Object.keys(nodeOrder || {}).map(k => k.toUpperCase()));
        graphEl.querySelectorAll('g.node').forEach(node => {
            const parts = (node.id || '').split('-'); // ["flowchart", "CLASS", "12"]
            const userId = parts.length >= 2 ? parts[1] : null;
            if (!userId) return;
            const upper = userId.toUpperCase();
            // Only nodes OUTSIDE the path get the .inactive dim filter. Path nodes keep the
            // dark active colour Mermaid applied via classDef, regardless of whether they
            // also have a step entry.
            if (!pathIds.has(upper)) {
                node.classList.add('inactive');
                return;
            }
            // Stamp the execution-order badge only on nodes that emitted a step — the badge
            // is the explicit-step signal, the dark colour is the "on the path" signal.
            const order = nodeOrder[upper] ?? nodeOrder[userId];
            if (order != null && !node.querySelector('.pipeline-step-badge')) {
                appendStepBadge(node, order);
            }
        });
        // Mermaid edge ids look like "L_GUARD_INTAKE_0" or "L-GUARD-INTAKE-0" depending on
        // version — first capture = source, second = target. We classify every edge:
        //   • Active path  → both endpoints in activeEdges set (computed server-side via BFS
        //     between consecutive active nodes). Gets a bright stroke so the journey from
        //     start to end is visually continuous, even across the dim Tier-1 cascade.
        //   • Inactive     → either endpoint outside the active set AND the edge isn't part
        //     of the active path. Dimmed.
        const activePathEdges = new Set(Object.keys(activeEdges || {}).map(k => k.toUpperCase()));
        graphEl.querySelectorAll('g.edgePath, path.flowchart-link, path[id^="L"]').forEach(edge => {
            const id = edge.id || '';
            const m = id.match(/^L[-_]([A-Za-z0-9]+)[-_]([A-Za-z0-9]+)[-_]\d+$/);
            if (!m) return;
            const a = m[1].toUpperCase();
            const b = m[2].toUpperCase();
            const edgeKey = `${a}->${b}`;
            const isPathEdge = activePathEdges.has(edgeKey);
            const wrap = edge.closest('g.edgePath');
            if (isPathEdge) {
                edge.classList.add('active-edge');
                if (wrap) wrap.classList.add('active-edge');
            } else if (!pathIds.has(a) || !pathIds.has(b)) {
                // Dim any edge that isn't on the active path AND has at least one endpoint
                // outside the journey set. Edges with BOTH endpoints in pathIds but not in
                // activePathEdges are theoretically possible (a node could be on the path via
                // a different edge) — leaving those at default styling is the safest read.
                edge.classList.add('inactive-edge');
                if (wrap) wrap.classList.add('inactive-edge');
            }
        });
    }

    function initPanZoomAndToolbar() {
        const viewport = document.getElementById('pipeline-graph-viewport');
        const graphEl = document.getElementById('pipeline-mermaid-graph');
        if (!viewport || !graphEl) return;

        // Always run on every render: tag inactive elements (SVG was rebuilt) and re-apply
        // the current transform so the user's zoom/pan survives a direction toggle.
        pzTagInactive(graphEl);
        pzApplyTransform();

        if (pzHandlersBound) return;
        pzHandlersBound = true;

        // ---- Wheel zoom (cursor-anchored) ----
        viewport.addEventListener('wheel', (e) => {
            e.preventDefault();
            const rect = viewport.getBoundingClientRect();
            const cx = e.clientX - rect.left;
            const cy = e.clientY - rect.top;
            const oldScale = pzScale;
            const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
            const newScale = pzClamp(oldScale * factor);
            if (newScale === oldScale) return;
            // Adjust pan so the point under the cursor stays anchored to that pixel.
            const ratio = newScale / oldScale;
            pzPanX = cx - (cx - pzPanX) * ratio;
            pzPanY = cy - (cy - pzPanY) * ratio;
            pzScale = newScale;
            pzApplyTransform();
        }, { passive: false });

        // ---- Drag-to-pan ----
        let isPanning = false;
        let startX = 0, startY = 0, origPanX = 0, origPanY = 0;
        viewport.addEventListener('mousedown', (e) => {
            if (e.button !== 0) return;                       // left button only
            if (e.target.closest('.pipeline-toolbar')) return; // don't grab toolbar clicks
            if (e.target.closest('g.node')) return;            // let node clicks through
            isPanning = true;
            startX = e.clientX;
            startY = e.clientY;
            origPanX = pzPanX;
            origPanY = pzPanY;
            viewport.classList.add('is-panning');
            e.preventDefault();
        });
        // Listen on window so a fast drag that exits the viewport still works.
        window.addEventListener('mousemove', (e) => {
            if (!isPanning) return;
            pzPanX = origPanX + (e.clientX - startX);
            pzPanY = origPanY + (e.clientY - startY);
            pzApplyTransform();
        });
        window.addEventListener('mouseup', () => {
            if (!isPanning) return;
            isPanning = false;
            viewport.classList.remove('is-panning');
        });

        // ---- Zoom buttons (center-anchored) ----
        function zoomBy(factor) {
            const oldScale = pzScale;
            const newScale = pzClamp(oldScale * factor);
            if (newScale === oldScale) return;
            const rect = viewport.getBoundingClientRect();
            const cx = rect.width / 2, cy = rect.height / 2;
            const ratio = newScale / oldScale;
            pzPanX = cx - (cx - pzPanX) * ratio;
            pzPanY = cy - (cy - pzPanY) * ratio;
            pzScale = newScale;
            pzApplyTransform();
        }
        const zoomInBtn = document.getElementById('pipeline-zoom-in');
        const zoomOutBtn = document.getElementById('pipeline-zoom-out');
        const zoomFitBtn = document.getElementById('pipeline-zoom-fit');
        if (zoomInBtn) zoomInBtn.addEventListener('click', () => zoomBy(1.2));
        if (zoomOutBtn) zoomOutBtn.addEventListener('click', () => zoomBy(1 / 1.2));
        if (zoomFitBtn) zoomFitBtn.addEventListener('click', () => {
            // Auto-fit: pick a scale that makes the whole SVG fit inside the viewport, then
            // center it. If we can't read intrinsic dimensions, fall back to a plain reset.
            const svg = graphEl.querySelector('svg');
            if (!svg) { pzScale = 1; pzPanX = 0; pzPanY = 0; pzApplyTransform(); return; }
            const vb = (svg.getAttribute('viewBox') || '').split(/\s+/);
            const naturalW = vb.length === 4 ? parseFloat(vb[2]) : 0;
            const naturalH = vb.length === 4 ? parseFloat(vb[3]) : 0;
            if (!naturalW || !naturalH) {
                pzScale = 1; pzPanX = 0; pzPanY = 0; pzApplyTransform(); return;
            }
            const rect = viewport.getBoundingClientRect();
            const sX = rect.width / naturalW;
            const sY = rect.height / naturalH;
            // 95% so there's a little padding around the edges.
            pzScale = pzClamp(Math.min(sX, sY) * 0.95);
            pzPanX = Math.max(0, (rect.width - naturalW * pzScale) / 2);
            pzPanY = Math.max(0, (rect.height - naturalH * pzScale) / 2);
            pzApplyTransform();
        });

        // ---- "Active only" toggle ----
        const activeBtn = document.getElementById('pipeline-toggle-active');
        if (activeBtn) activeBtn.addEventListener('click', () => {
            const on = viewport.classList.toggle('show-active-only');
            activeBtn.classList.toggle('is-toggled', on);
            activeBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        });

        // ---- Direction toggle (LR ⟷ TB) ----
        // Swap the direction token in the captured Mermaid source and re-run the renderer.
        // We reset pan/zoom because the layout dimensions change significantly.
        const dirBtn = document.getElementById('pipeline-toggle-direction');
        if (dirBtn) dirBtn.addEventListener('click', async () => {
            if (!pzOriginalSrc) return;
            const newDir = pzDirection === 'LR' ? 'TB' : 'LR';
            const swapped = pzOriginalSrc.replace(
                /^(\s*(?:flowchart|graph))\s+(LR|TB|TD|RL|BT)/im,
                (_, prefix) => `${prefix} ${newDir}`
            );
            pzOriginalSrc = swapped;
            graphEl.textContent = swapped;
            graphEl.removeAttribute('data-processed');
            pzScale = 1; pzPanX = 0; pzPanY = 0;
            rendered = false;
            pzDirection = newDir;
            dirBtn.classList.toggle('is-toggled', newDir === 'TB');
            dirBtn.setAttribute('aria-pressed', newDir === 'TB' ? 'true' : 'false');
            await renderGraph(); // re-tags inactive + reapplies transform via initPanZoomAndToolbar
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
