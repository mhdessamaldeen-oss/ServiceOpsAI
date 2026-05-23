(function () {
    window.copyToClipboard = function (btn, text) {
        if (!navigator.clipboard) return;
        navigator.clipboard.writeText(text).then(() => {
            const icon = btn.querySelector('i');
            const originalContent = btn.innerHTML;
            if (icon) {
                const originalClass = icon.className;
                icon.className = 'bi bi-check2 text-success';
                setTimeout(() => { icon.className = originalClass; }, 2000);
            } else {
                btn.textContent = 'Copied!';
                btn.classList.add('text-success');
                setTimeout(() => {
                    btn.innerHTML = originalContent;
                    btn.classList.remove('text-success');
                }, 2000);
            }
        });
    };
    let mermaidLoader;

    function cssValue(name, fallback) {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
    }

    function currentThemeSignature() {
        return [
            document.documentElement.getAttribute('data-bs-theme') || 'light',
            cssValue('--bg-card', '#ffffff'),
            cssValue('--text-main', '#0f172a'),
            cssValue('--text-muted', '#64748b'),
            cssValue('--primary-color', '#10b981'),
            cssValue('--primary-color-rgb', '16, 185, 129'),
            cssValue('--border-color', '#cbd5e1')
        ].join('|');
    }

    function getLuminance(hex) {
        try {
            if (!hex) return 0.5;
            let rgb = hex.trim();
            if (rgb.startsWith('rgb')) {
                const match = rgb.match(/\d+/g);
                if (!match) return 0.5;
                const r = parseInt(match[0], 10) / 255;
                const g = parseInt(match[1], 10) / 255;
                const b = parseInt(match[2], 10) / 255;
                return 0.2126 * r + 0.7152 * g + 0.0722 * b;
            }
            if (rgb.startsWith('#')) rgb = rgb.slice(1);
            if (rgb.length === 3) rgb = rgb.split('').map(c => c + c).join('');
            if (rgb.length !== 6) return 0.5;
            const r = parseInt(rgb.slice(0, 2), 16) / 255;
            const g = parseInt(rgb.slice(2, 4), 16) / 255;
            const b = parseInt(rgb.slice(4, 6), 16) / 255;
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        } catch {
            return 0.5;
        }
    }

    function getMermaidPalette() {
        const bgMain = cssValue('--bg-main', '#ffffff');
        const isDark = getLuminance(bgMain) < 0.45;
        
        // Read the primary color dynamically
        const primaryColor = cssValue('--primary-color', '#10b981');
        const primaryRgbText = cssValue('--primary-color-rgb', '16, 185, 129');
        const primaryRgb = primaryRgbText.split(',').map(n => {
            const val = parseInt(n.trim(), 10);
            return isNaN(val) ? 128 : val;
        });
        
        const textMain = cssValue('--text-main', isDark ? '#f8fafc' : '#1e293b');
        const bgBase = isDark ? [30, 41, 59] : [255, 255, 255];
        
        const blendToHex = (rgb, alpha) => {
            const r = Math.max(0, Math.min(255, Math.round(rgb[0] * alpha + bgBase[0] * (1 - alpha))));
            const g = Math.max(0, Math.min(255, Math.round(rgb[1] * alpha + bgBase[1] * (1 - alpha))));
            const b = Math.max(0, Math.min(255, Math.round(rgb[2] * alpha + bgBase[2] * (1 - alpha))));
            return '#' + [r, g, b].map(x => x.toString(16).padStart(2, '0')).join('');
        };

        return {
            inactiveFill: isDark ? '#1e293b' : '#ffffff',
            inactiveStroke: isDark ? '#334155' : '#e2e8f0',
            inactiveText: isDark ? '#94a3b8' : '#64748b',
            activeFill: blendToHex(primaryRgb, isDark ? 0.22 : 0.08),
            activeStroke: primaryColor,
            activeText: isDark ? '#ecfdf5' : primaryColor,
            finalFill: blendToHex(primaryRgb, isDark ? 0.35 : 0.14),
            finalStroke: primaryColor,
            finalText: isDark ? '#ffffff' : primaryColor,
            linkDefault: isDark ? '#475569' : '#e2e8f0',
            linkActive: primaryColor
        };
    }

    function applyMermaidPalette(definition) {
        const palette = getMermaidPalette();
        return definition
            .replaceAll('__INACTIVE_FILL__', palette.inactiveFill)
            .replaceAll('__INACTIVE_STROKE__', palette.inactiveStroke)
            .replaceAll('__INACTIVE_TEXT__', palette.inactiveText)
            .replaceAll('__ACTIVE_FILL__', palette.activeFill)
            .replaceAll('__ACTIVE_STROKE__', palette.activeStroke)
            .replaceAll('__ACTIVE_TEXT__', palette.activeText)
            .replaceAll('__FINAL_FILL__', palette.finalFill)
            .replaceAll('__FINAL_STROKE__', palette.finalStroke)
            .replaceAll('__FINAL_TEXT__', palette.finalText)
            .replaceAll('__LINK_DEFAULT__', palette.linkDefault)
            .replaceAll('__LINK_ACTIVE__', palette.linkActive);
    }

    async function loadMermaid() {
        if (!mermaidLoader) {
            mermaidLoader = import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs')
                .then((module) => {
                    const mermaid = module.default;
                    mermaid.initialize({
                        startOnLoad: false,
                        securityLevel: 'loose',
                        theme: 'base',
                        flowchart: {
                            useMaxWidth: true,
                            htmlLabels: true,
                            curve: 'basis'
                        }
                    });

                    window.copilotTraceNodeClick = window.copilotTraceNodeClick || function () { };
                    return mermaid;
                });
        }

        return mermaidLoader;
    }

    async function renderMermaidGraph(root) {
        const shells = root.querySelectorAll('.copilot-mermaid-shell');
        for (const shell of shells) {
            // Only render visible shells initially - hidden ones will render when tab is clicked
            const isVisible = !shell.classList.contains('d-none');
            if (isVisible) {
                await renderSingleMermaidGraph(root, shell);
            }
        }
    }
    
    // Expose function to render a specific shell by workflow ID
    window.renderWorkflowDiagram = async function(root, workflowId) {
        const shell = root.querySelector(`.copilot-mermaid-shell[data-workflow-id="${workflowId}"]`);
        if (shell) {
            await renderSingleMermaidGraph(root, shell);
        }
    }

    async function renderSingleMermaidGraph(root, shell) {
        const host = shell.querySelector('.copilot-mermaid-graph');
        const source = shell.querySelector('.copilot-mermaid-definition');
        const themeSignature = currentThemeSignature();

        if (!host || !source) {
            return;
        }

        const definition = source.textContent || '';
        if (!definition.trim()) {
            return;
        }

        if (host.dataset.rendered === 'true' && host.dataset.renderTheme === themeSignature) {
            initializeDiagramViewport(root, host);
            return;
        }

        disposeDiagramZoom(root);
        const mermaid = await loadMermaid();
        const renderId = host.dataset.traceMermaidId || `trace-mermaid-${Date.now()}`;
        const themedDefinition = applyMermaidPalette(definition);
        const { svg, bindFunctions } = await mermaid.render(renderId, themedDefinition);
        host.innerHTML = svg;
        bindFunctions?.(host);
        host.dataset.rendered = 'true';
        host.dataset.renderTheme = themeSignature;
        initializeDiagramViewport(shell, host);
    }

    function initializeDiagramViewport(shell, host) {
        const svg = host.querySelector('svg');
        if (!svg) {
            return;
        }

        applyRenderedSvgTheme(svg);

        if (typeof window.svgPanZoom !== 'function') {
            applyFallbackZoom(shell, shell._copilotDiagramZoom || 1);
            return;
        }

        if (shell._copilotPanZoom) {
            return;
        }

        // Safety checks for SVG dimensions to prevent "matrix not invertible" errors
        const svgRect = svg.getBoundingClientRect();
        const hasValidDimensions = svgRect.width > 0 && svgRect.height > 0;
        
        if (!hasValidDimensions) {
            console.warn('SVG has invalid dimensions, skipping pan/zoom initialization');
            applyFallbackZoom(shell, shell._copilotDiagramZoom || 1);
            return;
        }

        // Ensure SVG has valid viewBox before initializing pan zoom
        const viewBox = svg.getAttribute('viewBox');
        if (!viewBox) {
            const width = svg.getAttribute('width') || svgRect.width;
            const height = svg.getAttribute('height') || svgRect.height;
            if (width && height) {
                svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
            }
        }

        svg.removeAttribute('height');
        svg.setAttribute('width', '100%');
        svg.setAttribute('height', '100%');
        
        try {
            shell._copilotPanZoom = window.svgPanZoom(svg, {
                zoomEnabled: true,
                panEnabled: true,
                mouseWheelZoomEnabled: true,
                dblClickZoomEnabled: true,
                controlIconsEnabled: false,
                fit: true,
                center: true,
                minZoom: 0.2,
                maxZoom: 8
            });

            requestAnimationFrame(() => {
                try {
                    resetDiagramZoom(shell);
                } catch (e) {
                    console.warn('Error resetting diagram zoom:', e);
                }
            });
        } catch (e) {
            console.warn('Failed to initialize svg-pan-zoom:', e);
            applyFallbackZoom(shell, shell._copilotDiagramZoom || 1);
        }
    }

    function applyRenderedSvgTheme(svg) {
        // Only make the SVG canvas transparent — do NOT override text/stroke colors.
        // Correct colors are already baked in via the classDef placeholders at render time.
        svg.style.background = 'transparent';
        svg.style.maxWidth = '100%';
    }

    function disposeDiagramZoom(root) {
        if (root._copilotPanZoom) {
            root._copilotPanZoom.destroy();
            root._copilotPanZoom = null;
        }
    }

    function resetDiagramZoom(shell) {
        if (shell._copilotPanZoom) {
            try {
                shell._copilotPanZoom.resize();
                shell._copilotPanZoom.fit();
                shell._copilotPanZoom.center();
            } catch (e) {
                console.warn('Error during diagram zoom reset:', e);
                // Disable pan zoom if it's broken
                shell._copilotPanZoom.destroy?.();
                shell._copilotPanZoom = null;
                applyFallbackZoom(shell, 1);
            }
            return;
        }

        shell._copilotDiagramZoom = 1;
        applyFallbackZoom(shell, 1);
    }

    function applyFallbackZoom(shell, zoom) {
        const graph = shell.querySelector('.copilot-mermaid-graph');
        if (!graph) {
            return;
        }

        const nextZoom = Math.min(3, Math.max(0.35, Number(zoom || 1)));
        shell._copilotDiagramZoom = nextZoom;
        graph.style.transform = `scale(${nextZoom})`;
        graph.style.transformOrigin = 'top left';
    }

    function handleDiagramAction(root, action) {
        // Find the active mermaid shell (if multiple exist)
        const activeShell = root.querySelector('.copilot-mermaid-shell:not(.d-none)') || root.querySelector('.copilot-mermaid-shell');
        if (!activeShell) return;

        if (action === 'download') {
            downloadDiagramImage(activeShell).catch((error) => console.error('Unable to download workflow diagram.', error));
            return;
        }

        if (activeShell._copilotPanZoom) {
            try {
                if (action === 'zoom-in') {
                    activeShell._copilotPanZoom.zoomBy(1.2);
                } else if (action === 'zoom-out') {
                    activeShell._copilotPanZoom.zoomBy(0.82);
                } else if (action === 'reset') {
                    resetDiagramZoom(activeShell);
                }
            } catch (e) {
                console.warn('Error during zoom operation:', e);
                // Disable broken pan zoom and fallback
                try {
                    activeShell._copilotPanZoom.destroy?.();
                } catch (_) {}
                activeShell._copilotPanZoom = null;
                applyFallbackZoom(activeShell, 1);
            }
            return;
        }

        const currentZoom = activeShell._copilotDiagramZoom || 1;
        if (action === 'zoom-in') {
            applyFallbackZoom(activeShell, currentZoom + 0.15);
        } else if (action === 'zoom-out') {
            applyFallbackZoom(activeShell, currentZoom - 0.15);
        } else if (action === 'reset') {
            resetDiagramZoom(activeShell);
        }
    }

    async function downloadDiagramImage(root) {
        const svg = root.querySelector('.copilot-mermaid-graph svg');
        if (!svg) {
            return;
        }

        const clone = svg.cloneNode(true);
        const palette = getMermaidPalette();
        const box = svg.viewBox?.baseVal;
        const bounds = svg.getBBox ? svg.getBBox() : null;
        const width = Math.ceil(box?.width || bounds?.width || svg.clientWidth || 1600);
        const height = Math.ceil(box?.height || bounds?.height || svg.clientHeight || 900);

        clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
        clone.setAttribute('width', String(width));
        clone.setAttribute('height', String(height));
        if (!clone.getAttribute('viewBox')) {
            clone.setAttribute('viewBox', `0 0 ${width} ${height}`);
        }

        const background = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        background.setAttribute('x', '0');
        background.setAttribute('y', '0');
        background.setAttribute('width', '100%');
        background.setAttribute('height', '100%');
        background.setAttribute('fill', palette.bgCard);
        clone.insertBefore(background, clone.firstChild);

        const svgText = new XMLSerializer().serializeToString(clone);
        const svgBlob = new Blob([svgText], { type: 'image/svg+xml;charset=utf-8' });
        const url = URL.createObjectURL(svgBlob);

        try {
            const image = await loadImage(url);
            const canvas = document.createElement('canvas');
            const scale = 2;
            canvas.width = width * scale;
            canvas.height = height * scale;
            const context = canvas.getContext('2d');
            context.fillStyle = palette.bgCard;
            context.fillRect(0, 0, canvas.width, canvas.height);
            context.drawImage(image, 0, 0, canvas.width, canvas.height);

            const link = document.createElement('a');
            const traceId = root.id || 'copilot-trace';
            link.download = `${traceId}-workflow.png`;
            link.href = canvas.toDataURL('image/png');
            link.click();
        } finally {
            URL.revokeObjectURL(url);
        }
    }

    function loadImage(url) {
        return new Promise((resolve, reject) => {
            const image = new Image();
            image.onload = () => resolve(image);
            image.onerror = reject;
            image.src = url;
        });
    }

    function renderNodeInspector(root, nodeKey) {
        const panel = root.querySelector('.copilot-trace-node-panel');
        const nodeData = root._copilotTraceNodeData?.[nodeKey];

        if (!panel || !nodeData) {
            return;
        }

        panel.classList.remove('d-none');
        panel.querySelector('.copilot-trace-node-panel-title').textContent = nodeData.title || nodeKey;
        panel.querySelector('.copilot-trace-node-panel-summary').textContent = nodeData.summary || '';

        const status = panel.querySelector('.copilot-trace-node-panel-status');
        status.textContent = nodeData.status || '';
        status.className = 'copilot-trace-node-panel-status badge rounded-pill ' + (nodeData.used ? 'bg-success-subtle text-success-emphasis' : 'bg-secondary-subtle text-secondary-emphasis');

        panel.querySelector('.copilot-trace-node-before-label').textContent = nodeData.beforeLabel || 'Before';
        panel.querySelector('.copilot-trace-node-before-value').textContent = nodeData.beforeValue || '—';
        panel.querySelector('.copilot-trace-node-after-label').textContent = nodeData.afterLabel || 'After';
        panel.querySelector('.copilot-trace-node-after-value').textContent = nodeData.afterValue || '—';

        const substepsWrap = panel.querySelector('.copilot-trace-node-substeps');
        const substepsHost = panel.querySelector('.copilot-trace-node-substeps-list');
        const substeps = Array.isArray(nodeData.steps) ? nodeData.steps : [];
        const payloads = Array.isArray(nodeData.payloads) ? nodeData.payloads.filter(Boolean) : [];
        const derivedCards = uniqueMarkup([
            ...substeps.flatMap((step) => derivePayloadSubsteps(step)),
            ...payloads.flatMap((payload) => derivePayloadSubsteps({ technicalData: payload }))
        ]);

        if (substeps.length || derivedCards.length) {
            substepsWrap.classList.remove('d-none');
            substepsHost.innerHTML = [
                ...substeps.map((step) => renderSubstepCard(step)),
                ...derivedCards
            ].join('');
        } else {
            substepsWrap.classList.add('d-none');
            substepsHost.innerHTML = '';
        }

        const payloadWrap = panel.querySelector('.copilot-trace-node-payloads');
        const payloadHost = panel.querySelector('.copilot-trace-node-payloads-list');
        if (payloads.length) {
            payloadWrap.classList.remove('d-none');
            payloadHost.innerHTML = payloads.map((payload, index) => renderPayloadBlock(payload, index, {
                title: payloads.length > 1 ? `Section data ${index + 1}` : 'Section data',
                rawLabel: 'Raw section data',
                compact: true
            })).join('');
            if (window.Prism) {
                setTimeout(() => window.Prism.highlightAllUnder(payloadHost), 0);
            }
        } else {
            payloadWrap.classList.add('d-none');
            payloadHost.innerHTML = '';
        }

        panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    function renderSubstepCard(step, level = 0) {
        const task = normalizeSubstep(step);
        const subSteps = Array.isArray(step.subSteps) ? step.subSteps : [];
        const hasPayload = !!step.technicalData;
        const indent = level * 12;
        const levelClass = level > 0 ? ' is-child' : '';

        return `
                <div class="copilot-trace-substep-card${levelClass}" style="margin-left: ${indent}px">
                    <div class="copilot-trace-substep-header">
                        <div class="copilot-trace-substep-main">
                            <div class="copilot-trace-substep-title-row">
                                <div class="copilot-trace-substep-title">${escapeHtml(task.name)}</div>
                                ${task.codePath ? `<div class="copilot-trace-code-path">${escapeHtml(task.codePath)}</div>` : ''}
                            </div>
                            <div class="copilot-trace-substep-chip-row">
                                 <span class="copilot-trace-substep-chip">${escapeHtml(task.layer)}</span>
                                 <span class="copilot-trace-substep-chip ${task.statusClass}">${escapeHtml(task.status)}</span>
                                 ${hasPayload ? `<button class="copilot-trace-substep-payload-toggle is-active" data-payload-id="payload-${Math.random().toString(36).substr(2, 9)}"><i class="bi bi-braces me-1"></i> Technical Data</button>` : ''}
                             </div>
                        </div>
                        <span class="copilot-trace-substep-time">${task.elapsedMs} ms</span>
                    </div>
                    ${renderSubstepDetail(task)}
                    ${hasPayload ? renderStepPayloadBlock(step.technicalData) : ''}
                    ${subSteps.length ? `<div class="copilot-trace-substep-children">${subSteps.map(s => renderSubstepCard(s, level + 1)).join('')}</div>` : ''}
                </div>
            `;
    }

    function renderStepPayloadBlock(payload) {
        const language = looksLikeSql(payload) ? 'sql' : 'json';
        return `
            <div class="copilot-trace-substep-payload mt-3">
                <div class="copilot-trace-payload-section-label">
                    <div><i class="bi bi-code-square me-1"></i> Technical Payload</div>
                    <button class="btn btn-link btn-sm p-0 copilot-trace-payload-copy-btn text-decoration-none" onclick="copyToClipboard(this, \`${escapeHtml(payload).replace(/`/g, '\\`').replace(/\$/g, '\\$')}\`)">Copy</button>
                </div>
                <pre class="m-0"><code class="language-${language}">${escapeHtml(formatPayload(payload))}</code></pre>
            </div>
        `;
    }

    function normalizeSubstep(step) {
        const action = String(step?.action || 'Step').trim();
        const match = action.match(/^(.*?)\s*\[([^\]]+)\]\s*$/);
        const codePath = match?.[2]?.trim() || '';
        const name = prettifyTaskName(match?.[1] || action);
        const detailParts = splitStepDetail(step?.detail || '');
        const status = String(step?.status || 'Unknown');

        return {
            name,
            codePath,
            layer: step?.layer || 'Pipeline',
            status,
            statusClass: status.toLowerCase().includes('error')
                ? 'is-error'
                : status.toLowerCase().includes('warn')
                    ? 'is-warn'
                    : 'is-ok',
            elapsedMs: Number(step?.elapsedMs || 0),
            input: detailParts.input,
            output: detailParts.output,
            detail: detailParts.detail
        };
    }

    function prettifyTaskName(value) {
        return String(value || 'Step')
            .replace(/\s+/g, ' ')
            .replace(/^Query Execution:/i, 'Execute query:')
            .replace(/^Build Plan/i, 'Build execution plan')
            .replace(/^Execute Plan/i, 'Execute selected plan')
            .replace(/^Verify Response/i, 'Verify response')
            .trim();
    }

    function splitStepDetail(detail) {
        const text = String(detail || '').trim();
        if (!text) {
            return { input: '', output: '', detail: '' };
        }

        const inputMatch = text.match(/(?:^|\n)Input:\s*([\s\S]*?)(?=\nOutput:|$)/i);
        const outputMatch = text.match(/(?:^|\n)Output:\s*([\s\S]*?)(?=\n[A-Z][A-Za-z ]+:\s*|$)/i);
        const input = inputMatch?.[1]?.trim() || '';
        const output = outputMatch?.[1]?.trim() || '';
        const remainingDetail = text
            .replace(/(?:^|\n)Input:\s*[\s\S]*?(?=\nOutput:|$)/i, '')
            .replace(/(?:^|\n)Output:\s*[\s\S]*?(?=\n[A-Z][A-Za-z ]+:\s*|$)/i, '')
            .trim();

        return {
            input,
            output,
            detail: remainingDetail || (!input && !output ? text : '')
        };
    }

    function renderSubstepDetail(task) {
        const boxes = [];
        if (task.input) {
            boxes.push(renderSubstepInfoBox('Input', task.input));
        }

        if (task.output) {
            boxes.push(renderSubstepInfoBox('Output', task.output));
        }

        if (task.detail) {
            boxes.push(renderSubstepInfoBox('Details', task.detail));
        }

        if (!boxes.length) {
            return '<div class="copilot-trace-substep-detail is-empty">No extra detail was recorded for this task.</div>';
        }

        return `<div class="copilot-trace-substep-info-grid">${boxes.join('')}</div>`;
    }

    function renderSubstepInfoBox(label, value) {
        return `
            <div class="copilot-trace-substep-info-box">
                <div class="copilot-trace-substep-info-label">${escapeHtml(label)}</div>
                <div class="copilot-trace-substep-info-value">${escapeHtml(value)}</div>
            </div>
        `;
    }

    function renderPayloadBlock(payload, index, options = {}) {
        const language = looksLikeSql(payload) ? 'sql' : 'json';
        const sections = analyzePayload(payload, language);
        const title = options.title || `Payload ${Number(index || 0) + 1}`;
        const rawLabel = options.rawLabel || 'Raw payload';
        const extraClass = options.compact ? ' is-compact' : '';
        
        const sectionMarkup = [
            sections.input ? renderPayloadSection('Incoming', sections.input, sections.inputLanguage || language, 'is-input', 'bi-input-cursor') : '',
            sections.output ? renderPayloadSection('Produced', sections.output, sections.outputLanguage || language, 'is-output', 'bi-lightning-charge') : '',
            sections.context ? renderPayloadSection('Context', sections.context, sections.contextLanguage || language, 'is-context', 'bi-info-circle') : ''
        ].filter(Boolean).join('');

        return `
            <div class="copilot-trace-payload-block${extraClass} overflow-hidden rounded-3">
                <div class="copilot-trace-payload-toolbar">
                    <div class="d-flex align-items-center gap-2">
                        <i class="bi bi-cpu"></i>
                        <span>${escapeHtml(title)}</span>
                    </div>
                    <span class="badge bg-soft-primary text-primary px-2 py-1" style="font-size: 0.6rem;">${language.toUpperCase()}</span>
                </div>
                <div class="copilot-trace-payload-layout">
                    ${sectionMarkup}
                    <div class="copilot-trace-payload-raw">
                        <div class="copilot-trace-payload-section-label">
                            <div><i class="bi bi-code-slash me-1"></i> ${escapeHtml(rawLabel)}</div>
                            <button class="btn btn-link btn-sm p-0 copilot-trace-payload-copy-btn text-decoration-none" onclick="copyToClipboard(this, \`${escapeHtml(payload).replace(/`/g, '\\`').replace(/\$/g, '\\$')}\`)">Copy</button>
                        </div>
                        <pre class="m-0"><code class="language-${language}">${escapeHtml(formatPayload(payload))}</code></pre>
                    </div>
                </div>
            </div>
        `;
    }

    function renderPayloadSection(label, value, language, modifierClass, iconClass) {
        return `
            <div class="copilot-trace-payload-section ${modifierClass}">
                <div class="copilot-trace-payload-section-label">
                    <div><i class="bi ${iconClass} me-1"></i> ${escapeHtml(label)}</div>
                    <button class="btn btn-link btn-sm p-0 copilot-trace-payload-copy-btn text-decoration-none" onclick="copyToClipboard(this, \`${escapeHtml(value).replace(/`/g, '\\`').replace(/\$/g, '\\$')}\`)">Copy</button>
                </div>
                <pre class="m-0"><code class="language-${language}">${escapeHtml(value)}</code></pre>
            </div>
        `;
    }

    function analyzePayload(payload, language) {
        if (language === 'sql') {
            return {
                input: '',
                output: payload,
                context: ''
            };
        }

        const parsed = parseJson(payload);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
            return {
                input: '',
                output: '',
                context: formatPayload(payload)
            };
        }

        const inputKeys = ['question', 'searchQuery', 'request', 'input', 'before', 'conversationContext', 'primaryEntity', 'entities', 'fields', 'groupBy', 'filters', 'sorts', 'aggregations', 'joins', 'intentPlan', 'queryModel', 'decision'];
        const outputKeys = ['answer', 'response', 'output', 'after', 'summary', 'result', 'results', 'generatedSql', 'sql', 'commandText', 'structuredRows', 'structuredColumns', 'totalCount', 'rowCount', 'requiresClarification', 'clarificationQuestion'];

        const inputSection = pickPayloadSection(parsed, inputKeys);
        const outputSection = pickPayloadSection(parsed, outputKeys);
        const contextSection = omitPayloadKeys(parsed, [...inputKeys, ...outputKeys]);

        return {
            input: stringifyPayloadSection(inputSection),
            inputLanguage: 'json',
            output: stringifyPayloadSection(outputSection),
            outputLanguage: inferPayloadLanguage(outputSection),
            context: stringifyPayloadSection(contextSection),
            contextLanguage: 'json'
        };
    }

    function pickPayloadSection(payload, keys) {
        return keys.reduce((result, key) => {
            const value = getAny(payload, key);
            if (value !== undefined) {
                result[key] = value;
            }

            return result;
        }, {});
    }

    function omitPayloadKeys(payload, excludedKeys) {
        return Object.entries(payload).reduce((result, [key, value]) => {
            const isExcluded = excludedKeys.some((excludedKey) => excludedKey.toLowerCase() === key.toLowerCase());
            if (!isExcluded) {
                result[key] = value;
            }

            return result;
        }, {});
    }

    function stringifyPayloadSection(section) {
        if (!section || (typeof section === 'object' && Object.keys(section).length === 0)) {
            return '';
        }

        if (typeof section === 'string') {
            return section;
        }

        return JSON.stringify(section, null, 2);
    }

    function inferPayloadLanguage(section) {
        if (!section) {
            return 'json';
        }

        if (typeof section === 'object') {
            const raw = getAny(section, 'generatedSql', 'sql', 'commandText') || '';
            return looksLikeSql(raw) ? 'sql' : 'json';
        }

        return looksLikeSql(section) ? 'sql' : 'json';
    }

    function derivePayloadSubsteps(step) {
        const payload = parseJson(step?.technicalData);
        if (!payload) {
            return [];
        }

        const subrequests = getAny(payload, 'subrequests');
        if (Array.isArray(subrequests)) {
            return subrequests.map((item) => renderDerivedCard(
                `Part ${getAny(item, 'index')}: ${getAny(item, 'kind') || 'Unknown'}`,
                getAny(item, 'source') || 'Subrequest',
                `Text: "${getAny(item, 'text') || ''}"\nReason: ${getAny(item, 'reason') || 'No reason recorded.'}${getAny(item, 'toolName') && getAny(item, 'toolName') !== 'none' ? `\nTool: ${getAny(item, 'toolName')}` : ''}`
            ));
        }

        const intentPlan = getAny(payload, 'intentPlan');
        const queryModel = getAny(payload, 'queryModel');
        const decision = getAny(payload, 'decision');
        if (intentPlan || queryModel || decision) {
            return [
                ...renderDataIntentCards(decision),
                ...renderDataIntentCards(intentPlan),
                ...renderDataIntentCards(queryModel)
            ];
        }

        if (getAny(payload, 'primaryEntity') || getAny(payload, 'operation') || asArray(getAny(payload, 'fields', 'projections')).length) {
            return renderDataIntentCards(payload);
        }

        const dataIntent = getAny(payload, 'dataIntent');
        const ticketQueryPlan = getAny(payload, 'ticketQueryPlan');
        if (dataIntent || ticketQueryPlan) {
            return [
                ...renderDataIntentCards(dataIntent),
                ...(ticketQueryPlan ? [renderDerivedCard(
                    'Legacy analytics fallback plan',
                    getAny(ticketQueryPlan, 'targetView') || 'Query strategy',
                    `Intent: ${getAny(ticketQueryPlan, 'intent') || ''}\nMax results: ${getAny(ticketQueryPlan, 'maxResults') ?? 'not set'}\nSort: ${getAny(ticketQueryPlan, 'sortBy') || 'default'} ${getAny(ticketQueryPlan, 'sortDirection') || ''}`
                )] : [])
            ];
        }

        const sql = getAny(payload, 'generatedSql', 'sql', 'commandText');
        if (sql) {
            return [renderDerivedCard(
                'SQL execution',
                'Catalog executor',
                sql
            )];
        }

        return [];
    }

    function renderDataIntentCards(intent) {
        if (!intent) {
            return [];
        }

        const fields = formatProjectionList(getAny(intent, 'fields', 'projections', 'columns'), 'Default fields');
        const filters = formatFilterList([
            ...asArray(getAny(intent, 'filters', 'predicates')),
            ...asArray(getAny(intent, 'havingFilters', 'havingPredicates'))
        ]);
        const joins = formatJoinList(getAny(intent, 'joins', 'joinFragments'));
        const validation = asArray(getAny(intent, 'validationMessages')).length
            ? asArray(getAny(intent, 'validationMessages')).join('\n')
            : 'Passed catalog validation';
        const sorts = formatSortList(getAny(intent, 'sorts', 'orderBy'));
        const groupBy = formatListValue(getAny(intent, 'groupBy'), 'No grouping');
        const requiresClarification = Boolean(getAny(intent, 'requiresClarification'));
        const clarificationQuestion = getAny(intent, 'clarificationQuestion') || '';

        return [
            renderDerivedCard(
                'Catalog operation',
                getAny(intent, 'primaryEntity') || 'DataQuery',
                `Operation: ${getAny(intent, 'operation') || 'unknown'}\nOutput: ${getAny(intent, 'outputShape') || 'unknown'}\nLimit: ${getAny(intent, 'limit') ?? 'not set'}`
            ),
            renderDerivedCard('Fields selected', 'Catalog fields', fields),
            renderDerivedCard('Filters and joins', 'Catalog validation', `Filters:\n${filters}\n\nJoins:\n${joins}\n\nGroup by: ${groupBy}\nSort: ${sorts}`),
            renderDerivedCard(
                requiresClarification ? 'Clarification required' : 'Validation result',
                requiresClarification ? 'Warn' : 'Ok',
                requiresClarification ? (clarificationQuestion || validation) : validation
            )
        ];
    }

    function getAny(source, ...keys) {
        if (!source || typeof source !== 'object') {
            return undefined;
        }

        for (const key of keys) {
            if (Object.prototype.hasOwnProperty.call(source, key)) {
                return source[key];
            }

            const matchedKey = Object.keys(source).find((candidate) => candidate.toLowerCase() === key.toLowerCase());
            if (matchedKey) {
                return source[matchedKey];
            }
        }

        return undefined;
    }

    function asArray(value) {
        if (Array.isArray(value)) {
            return value;
        }

        if (value === undefined || value === null || value === '') {
            return [];
        }

        return [value];
    }

    function formatListValue(value, emptyText) {
        const items = asArray(value).map((item) => typeof item === 'string' ? item : JSON.stringify(item)).filter(Boolean);
        return items.length ? items.join(', ') : emptyText;
    }

    function formatProjectionList(value, emptyText) {
        const items = asArray(value).map((field) => {
            if (typeof field === 'string') {
                return field;
            }

            const source = [getAny(field, 'sourceEntity'), getAny(field, 'sourceField')].filter(Boolean).join('.');
            const expression = source || getAny(field, 'sqlExpression') || '';
            const output = getAny(field, 'outputName');
            return output ? `${output} <= ${expression}` : expression;
        }).filter(Boolean);

        return items.length ? items.join('\n') : emptyText;
    }

    function formatFilterList(filters) {
        const items = asArray(filters).flat().map((filter) => {
            if (typeof filter === 'string') {
                return filter;
            }

            const sqlFragment = getAny(filter, 'sqlFragment');
            if (sqlFragment) {
                const parameters = asArray(getAny(filter, 'parameters')).map((parameter) => `${getAny(parameter, 'name')}=${getAny(parameter, 'value')}`).join(', ');
                return parameters ? `${sqlFragment} (${parameters})` : sqlFragment;
            }

            const entity = getAny(filter, 'entity') || '';
            const field = getAny(filter, 'field') || '';
            const operator = getAny(filter, 'operator') || '';
            const value = getAny(filter, 'value') ?? '';
            return `${entity}.${field} ${operator} ${value}`.trim();
        }).filter(Boolean);

        return items.length ? items.join('\n') : 'No filters';
    }

    function formatJoinList(value) {
        const items = asArray(value).map((join) => {
            if (typeof join === 'string') {
                return join;
            }

            return `${getAny(join, 'fromEntity') || ''} -> ${getAny(join, 'toEntity') || ''} (${getAny(join, 'relationship') || ''})`.trim();
        }).filter(Boolean);

        return items.length ? items.join('\n') : 'No joins';
    }

    function formatSortList(value) {
        const items = asArray(value).map((sort) => {
            if (typeof sort === 'string') {
                return sort;
            }

            const sqlExpression = getAny(sort, 'sqlExpression');
            if (sqlExpression) {
                return `${sqlExpression} ${getAny(sort, 'direction') || ''}`.trim();
            }

            return `${getAny(sort, 'entity') || ''}.${getAny(sort, 'field') || ''} ${getAny(sort, 'direction') || ''}`.trim();
        }).filter(Boolean);

        return items.length ? items.join('\n') : 'No sort';
    }

    function renderDerivedCard(title, meta, detail) {
        return `
            <div class="copilot-trace-substep-card copilot-trace-derived-card">
                <div class="copilot-trace-substep-header">
                    <div class="copilot-trace-substep-main">
                        <div class="copilot-trace-substep-title">${escapeHtml(title)}</div>
                        <div class="copilot-trace-substep-meta">${escapeHtml(meta)}</div>
                    </div>
                    <span class="copilot-trace-substep-time">derived</span>
                </div>
                <div class="copilot-trace-substep-detail">${escapeHtml(detail)}</div>
            </div>
        `;
    }

    function renderStoryPayload(payload) {
        const derivedCards = uniqueMarkup(derivePayloadSubsteps({ technicalData: payload }));
        const derivedMarkup = derivedCards.length
            ? `
                <div class="copilot-story-payload-derived">
                    <div class="copilot-story-payload-section-label">What happened in this step</div>
                    <div class="copilot-story-payload-derived-grid">
                        ${derivedCards.join('')}
                    </div>
                </div>
            `
            : '';

        return `
            ${derivedMarkup}
            ${renderPayloadBlock(payload, 0, { title: 'Step data flow', rawLabel: 'Raw step payload', compact: true })}
        `;
    }

    function initializeStoryPayloads(root) {
        root.querySelectorAll('[data-copilot-story-payload]').forEach((shell) => {
            if (shell.dataset.storyPayloadBound === 'true') {
                return;
            }

            const source = shell.querySelector('.copilot-story-payload-source');
            const host = shell.querySelector('.copilot-story-payload-host');
            const payload = source?.value?.trim();

            if (!host || !payload) {
                shell.dataset.storyPayloadBound = 'true';
                return;
            }

            host.innerHTML = renderStoryPayload(payload);
            if (window.Prism) {
                setTimeout(() => window.Prism.highlightAllUnder(host), 0);
            }

            shell.dataset.storyPayloadBound = 'true';
        });
    }

    function parseJson(value) {
        if (!value || typeof value !== 'string') {
            return null;
        }

        const trimmed = value.trim();
        if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) {
            return null;
        }

        try {
            return JSON.parse(trimmed);
        } catch {
            return null;
        }
    }

    function formatPayload(value) {
        const parsed = parseJson(value);
        return parsed ? JSON.stringify(parsed, null, 2) : value;
    }

    function looksLikeSql(value) {
        return /^\s*(select|with|update|insert|delete)\b/i.test(String(value || ''));
    }

    function uniqueMarkup(items) {
        const seen = new Set();
        return items.filter((item) => {
            if (!item || seen.has(item)) {
                return false;
            }

            seen.add(item);
            return true;
        });
    }

    function setTraceView(root, mode) {
        const storyView = root.querySelector('.copilot-story-view');
        const treeView = root.querySelector('.copilot-tree-view');
        const buttons = root.querySelectorAll('.copilot-trace-view-btn');
        const showTree = mode === 'tree';

        if (!storyView || !treeView) {
            return;
        }

        root.classList.toggle('is-tree-view', showTree);
        root.classList.toggle('is-story-view', !showTree);

        storyView.style.display = showTree ? 'none' : 'block';
        treeView.style.display = showTree ? 'block' : 'none';

        buttons.forEach((button) => {
            const active = button.dataset.traceViewTarget === mode;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
        });

        if (showTree) {
            renderMermaidGraph(root).catch((error) => {
                const host = root.querySelector('.copilot-mermaid-graph');
                if (host) {
                    host.innerHTML = `<div class="alert alert-warning mb-0 small">Unable to render workflow diagram: ${error.message}</div>`;
                }
            });
        }
    }

    function activateTraceViewFromButton(button) {
        const root = button?.closest?.('.copilot-trace-detail-root');
        const mode = button?.dataset?.traceViewTarget;
        if (!root || !mode) {
            return;
        }

        if (root.dataset.traceToggleBound !== 'true') {
            initializeTraceRoot(root);
        }

        setTraceView(root, mode);
    }

    window.switchCopilotTraceView = function (rootIdOrElement, mode) {
        const root = typeof rootIdOrElement === 'string'
            ? document.getElementById(rootIdOrElement)
            : rootIdOrElement;

        if (!root || !mode) {
            return;
        }

        if (root.dataset.traceToggleBound !== 'true') {
            initializeTraceRoot(root);
        }

        setTraceView(root, mode);
    };

    function bindWorkflowTabs(root) {
        const tabs = root.querySelectorAll('.copilot-workflow-tab');
        if (!tabs.length) return;

        tabs.forEach(tab => {
            tab.addEventListener('click', async () => {
                const targetId = tab.dataset.workflowTab;
                
                // Update active tab
                tabs.forEach(t => t.classList.toggle('is-active', t === tab));
                
                // Update active shell
                const shells = root.querySelectorAll('.copilot-mermaid-shell');
                for (const shell of shells) {
                    const active = shell.dataset.workflowId === targetId;
                    const wasHidden = shell.classList.contains('d-none');
                    shell.classList.toggle('d-none', !active);
                    
                    if (active) {
                        // Check if diagram needs to be rendered (first time visible)
                        const host = shell.querySelector('.copilot-mermaid-graph');
                        const needsRender = !host || host.dataset.rendered !== 'true';
                        
                        if (needsRender) {
                            // Render the diagram first
                            await renderSingleMermaidGraph(root, shell);
                        }
                        
                        // Then reset zoom after a short delay to ensure SVG is ready
                        setTimeout(() => {
                            try {
                                resetDiagramZoom(shell);
                            } catch (e) {
                                console.warn('Error resetting zoom:', e);
                            }
                        }, 50);
                    }
                };

                // Update active node inspector data
                const nodeDataScript = root.querySelector(`.copilot-trace-node-data[data-workflow-id="${targetId}"]`);
                if (nodeDataScript) {
                    try {
                        root._copilotTraceNodeData = JSON.parse(nodeDataScript.textContent || '{}');
                        // Hide inspector when switching tabs to avoid confusion
                        root.querySelector('.copilot-trace-node-panel')?.classList.add('d-none');
                    } catch (e) { console.error(e); }
                }
            });
        });
    }

    function bindDiagramControls(root) {
        root.querySelectorAll('[data-copilot-diagram-action]').forEach((button) => {
            if (button.dataset.copilotDiagramBound === 'true') {
                return;
            }

            button.addEventListener('click', () => handleDiagramAction(root, button.dataset.copilotDiagramAction));
            button.dataset.copilotDiagramBound = 'true';
        });
    }

    function bindInspectorEvents(root) {
        root.addEventListener('click', (e) => {
            const toggle = e.target.closest('.copilot-trace-substep-payload-toggle');
            if (toggle) {
                const card = toggle.closest('.copilot-trace-substep-card');
                const payload = card?.querySelector('.copilot-trace-substep-payload');
                if (payload) {
                    payload.classList.toggle('d-none');
                    toggle.classList.toggle('is-active');
                    if (window.Prism && !payload.classList.contains('d-none')) {
                        window.Prism.highlightAllUnder(payload);
                    }
                }
                return;
            }

            const copy = e.target.closest('.copilot-copy-payload');
            if (copy) {
                const text = copy.dataset.payload;
                if (text) {
                    navigator.clipboard.writeText(text).then(() => {
                        const originalText = copy.textContent;
                        copy.textContent = 'Copied!';
                        copy.classList.add('text-success');
                        setTimeout(() => {
                            copy.textContent = originalText;
                            copy.classList.remove('text-success');
                        }, 2000);
                    });
                }
            }
        });
    }

    function initializeTraceRoot(root) {
        if (!root || root.dataset.traceToggleBound === 'true') {
            return;
        }

        bindInspectorEvents(root);

        const nodeDataScript = root.querySelector('.copilot-trace-node-data');
        if (nodeDataScript) {
            try {
                root._copilotTraceNodeData = JSON.parse(nodeDataScript.textContent || '{}');
            } catch (e) {
                console.error('Failed to parse copilot trace node data', e);
            }
        }


        const defaultMode = (root.dataset.defaultTraceView || 'story').toLowerCase() === 'tree' ? 'tree' : 'story';
        setTraceView(root, defaultMode);

        root.querySelectorAll('.copilot-trace-view-btn').forEach((btn) => {
            if (btn.dataset.traceViewBound === 'true') {
                return;
            }

            btn.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();
                activateTraceViewFromButton(btn);
            });
            btn.dataset.traceViewBound = 'true';
        });

        bindDiagramControls(root);
        bindWorkflowTabs(root);
        initializeStoryPayloads(root);

        root.dataset.traceToggleBound = 'true';
    }

    window.initializeCopilotTraceDetails = function (container) {
        const scope = container || document;
        const roots = scope.matches?.('.copilot-trace-detail-root')
            ? [scope]
            : Array.from(scope.querySelectorAll?.('.copilot-trace-detail-root') || []);

        roots.forEach(initializeTraceRoot);
    };

    window.copilotTraceNodeClick = function (nodeId) {
        console.log('Copilot trace node clicked:', nodeId);
        
        const separatorIndex = nodeId.indexOf('__');
        if (separatorIndex === -1) {
            return;
        }

        const nodeKey = nodeId.slice(0, separatorIndex);
        const traceGraphKey = nodeId.slice(separatorIndex + 2);
        let root = document.querySelector(`.copilot-trace-detail-root[data-trace-graph-key="${traceGraphKey}"]`);
        if (!root) {
            const parentTraceGraphKey = traceGraphKey.replace(/_sub_\d+$/, '');
            root = document.querySelector(`.copilot-trace-detail-root[data-trace-graph-key="${parentTraceGraphKey}"]`);
        }

        if (!root) {
            console.warn('Trace root not found for graph key:', traceGraphKey);
            return;
        }

        // Visual feedback
        const graphHost = root.querySelector('.copilot-mermaid-graph');
        if (graphHost) {
            graphHost.style.cursor = 'wait';
            setTimeout(() => { graphHost.style.cursor = ''; }, 400);
        }

        renderNodeInspector(root, nodeKey);
    };

    let themeRenderTimer;
    const themeObserver = new MutationObserver(() => {
        clearTimeout(themeRenderTimer);
        themeRenderTimer = setTimeout(() => {
            document.querySelectorAll('.copilot-trace-detail-root.is-tree-view').forEach((root) => {
                const host = root.querySelector('.copilot-mermaid-graph');
                if (host) {
                    host.dataset.rendered = 'false';
                }

                renderMermaidGraph(root).catch((error) => console.error('Unable to refresh workflow diagram theme.', error));
            });
        }, 80);
    });

    // Observe both <html> attributes and <body> style (platform injects CSS vars into :root via <style>)
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['data-bs-theme', 'class', 'style']
    });
    themeObserver.observe(document.body, {
        attributes: true,
        attributeFilter: ['class', 'style']
    });
    // Also observe <head> <style> changes for server-injected CSS variable updates
    const headStyleObserver = new MutationObserver(() => {
        clearTimeout(themeRenderTimer);
        themeRenderTimer = setTimeout(() => {
            document.querySelectorAll('.copilot-trace-detail-root.is-tree-view').forEach((root) => {
                const host = root.querySelector('.copilot-mermaid-graph');
                if (host) host.dataset.rendered = 'false';
                renderMermaidGraph(root).catch((error) => console.error('Unable to refresh diagram on style change.', error));
            });
        }, 150);
    });
    const headStyle = document.head?.querySelector('style');
    if (headStyle) headStyleObserver.observe(headStyle, { characterData: true, childList: true, subtree: true });

    document.addEventListener('DOMContentLoaded', () => {
        window.initializeCopilotTraceDetails(document);
    });

    document.addEventListener('click', (event) => {
        const button = event.target.closest?.('.copilot-trace-view-btn[data-trace-view-target]');
        if (!button) {
            return;
        }

        event.preventDefault();
        activateTraceViewFromButton(button);
    });

    function escapeHtml(value) {
        return String(value ?? '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }
})();
