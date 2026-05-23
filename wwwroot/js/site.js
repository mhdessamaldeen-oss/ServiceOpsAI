// Premium DataTables Initialization
function initPremiumDataTable(tableSelector, options = {}) {
    const $table = $(tableSelector);
    if ($table.length === 0) return;
    const tableElement = $table.get(0);
    const tableId = $table.attr('id') || $table.data('state-key') || `table-${Math.random().toString(36).slice(2, 10)}`;
    const storageKey = `supportflow.datatable.v2.${window.location.pathname}.${tableId}`;
    const configuredPageLength = parseInt($table.data('page-length'), 10);
    const defaultPageLength = Number.isNaN(configuredPageLength) ? 10 : configuredPageLength;
    const pagingEnabled = $table.data('paging') !== false;
    const infoEnabled = $table.data('info') !== false;
    const pageLengthControlEnabled = $table.data('page-length-control') !== false;

    const isRtl = document.documentElement.dir === 'rtl';

    const defaultOptions = {
        processing: true,
        responsive: true,
        pageLength: defaultPageLength,
        paging: pagingEnabled,
        info: infoEnabled,
        lengthChange: pageLengthControlEnabled,
        stateSave: true,
        stateDuration: -1,
        destroy: true,
        layout: {
            topStart: {
                search: {
                    placeholder: isRtl ? 'بحث سريع...' : 'QUICK SEARCH...'
                }
            },
            topEnd: pageLengthControlEnabled ? 'pageLength' : null,
            bottomStart: infoEnabled ? 'info' : null,
            bottomEnd: pagingEnabled ? 'paging' : null
        },
        language: isRtl ? {
            url: "https://cdn.datatables.net/plug-ins/2.0.3/i18n/ar.json",
            search: "",
            processing: "جاري المعالجة...",
            lengthMenu: "_MENU_ عنصر في الصفحة",
            info: "عرض _START_ إلى _END_ من أصل _TOTAL_ سجل",
            infoEmpty: "لا توجد سجلات متاحة",
            infoFiltered: "(تمت التصفية من إجمالي _MAX_ سجل)",
            paginate: {
                first: "الأول",
                last: "الأخير",
                next: "التالي",
                previous: "السابق"
            }
        } : {
            search: "",
            processing: '<div class="spinner-border text-primary" role="status"><span class="visually-hidden">Loading...</span></div>'
        },
        lengthMenu: [
            [10, 25, 50, 100, -1],
            [10, 25, 50, 100, isRtl ? 'الكل' : 'All']
        ],
        orderCellsTop: true, // Only place sort icons on primary header row
        order: [],
        columnDefs: [
            { targets: 'no-sort', orderable: false }
        ],
        stateSaveCallback: function(settings, data) {
            localStorage.setItem(storageKey, JSON.stringify(data));
        },
        stateLoadCallback: function() {
            const raw = localStorage.getItem(storageKey);
            return raw ? JSON.parse(raw) : null;
        }
    };

    const finalOptions = $.extend(true, {}, defaultOptions, options);

    // 1. Add Column Search Row in thead if requested
    if (finalOptions.columnSearch) {
        $table.find('thead .search-row').remove(); // Clear existing if any
        let searchRow = $('<tr class="search-row animate__animated animate__fadeIn"></tr>');
        $table.find('thead th').each(function(i) {
            const title = $(this).text().trim();
            const isNoSort = $(this).hasClass('no-sort') || $(this).data('col') === 'actions' || $(this).find('input[type="checkbox"]').length > 0;
            
            let th = $('<th class="py-3 px-2"></th>');
            if (!isNoSort) {
                const placeholder = isRtl ? `تصفية ${title}...` : `Filter ${title}...`;
                const $input = $(`<input type="text" class="form-control column-search-input" placeholder="${placeholder}" data-index="${i}" />`);
                
                // Prevent sorting when clicking/interacting inside the search box
                $input.on('click mousedown', function(e) { e.stopPropagation(); });
                
                th.append($input);
            }
            searchRow.append(th);
        });
        $table.find('thead').append(searchRow);
    }

    // 2. Initialize DataTable
    const table = $table.DataTable(finalOptions);

    // 3. Add Buttons after initialization to ensure column visibility is ready
    if ($.fn.dataTable.Buttons && $table.data('table-tools') !== false) {
        try {
            new $.fn.dataTable.Buttons(table, {
                buttons: [
                    {
                        extend: 'copyHtml5',
                        className: 'btn btn-sm feature-table-toolbtn',
                        exportOptions: { columns: ':visible:not(.no-sort)' }
                    },
                    {
                        extend: 'excelHtml5',
                        className: 'btn btn-sm feature-table-toolbtn',
                        exportOptions: { columns: ':visible:not(.no-sort)' }
                    },
                    {
                        extend: 'print',
                        className: 'btn btn-sm feature-table-toolbtn',
                        exportOptions: { columns: ':visible:not(.no-sort)' }
                    },
                    {
                        extend: 'colvis',
                        className: 'btn btn-sm feature-table-toolbtn',
                        postfixButtons: ['colvisRestore']
                    }
                ]
            });
            table.buttons().container().appendTo($table.closest('.feature-table-shell').find('.table-actions-slot'));
        } catch (e) {
            console.warn('DataTable buttons failed to init:', e);
        }
    }

    const hasSavedState = !!localStorage.getItem(storageKey);
    if (!hasSavedState && pagingEnabled) {
        table.page.len(defaultPageLength).draw(false);
    }

    // 3. Bind Column Search Inputs (Optimized for 2.0)
    if (finalOptions.columnSearch) {
        $table.on('keyup change', '.column-search-input', function() {
            const colIndex = $(this).data('index');
            table.column(colIndex).search(this.value).draw();
        });
    }

    // 4. Handle length change for tables with manual (server-side) paging
    table.on('length.dt', function(e, settings, len) {
        const shell = $table.closest('[data-feature-table-shell]');
        if (shell.length > 0 && shell.find('.ajax-pagination-link').length > 0) {
            const manualLink = shell.find(`.ajax-pagination-link[data-page-size="${len}"]`);
            if (manualLink.length > 0) {
                manualLink.click();
            } else {
                // Fallback: Try to adjust existing pagination URL
                const baseLink = shell.find('.ajax-pagination-link').first().prop('href');
                if (baseLink) {
                    const url = new URL(baseLink, window.location.origin);
                    url.searchParams.set('request.PageSize', len);
                    url.searchParams.set('request.PageNumber', 1);
                    
                    // Trigger custom AJAX load
                    const containerSelector = '[data-feature-table-shell]';
                    const targetContainer = shell.length ? shell : $(containerSelector);
                    
                    if (targetContainer.length) {
                        targetContainer.css('opacity', '0.5').css('pointer-events', 'none');
                        $.ajax({
                            url: url.toString(),
                            headers: { 'X-Requested-With': 'XMLHttpRequest' },
                            success: function(html) {
                                targetContainer.html(html).css('opacity', '1').css('pointer-events', 'auto');
                                // Re-init tables handled by global handler
                            }
                        });
                    }
                }
            }
        }
    });

    return table;
}

// Global Auto-Init for tables with .datatable-premium class
$(document).ready(function() {
    $('.datatable-premium').each(function() {
        const hasColumnSearch = $(this).data('column-search') !== false;
        initPremiumDataTable(this, {
            columnSearch: hasColumnSearch
        });
    });
});

document.addEventListener('shown.bs.tab', function (event) {
    const targetSelector = event.target.getAttribute('data-bs-target');
    if (!targetSelector || !window.jQuery || !$.fn.dataTable) {
        return;
    }

    const targetPane = document.querySelector(targetSelector);
    if (!targetPane) {
        return;
    }

    $(targetPane).find('.datatable-premium').each(function () {
        if ($.fn.dataTable.isDataTable(this)) {
            $(this).DataTable().columns.adjust().draw(false);
        }
    });
});

document.addEventListener('change', function (event) {
    const toggle = event.target.closest('[data-column-search-toggle="true"]');
    if (!toggle) {
        return;
    }

    const selector = toggle.getAttribute('data-table-target');
    if (!selector) {
        return;
    }

    const table = document.querySelector(selector);
    if (!table) {
        return;
    }

    const searchRow = table.querySelector('.search-row');
    if (!searchRow) {
        return;
    }

    searchRow.classList.toggle('d-none', !toggle.checked);
});

// AJAX Grid Support
$(document).on('click', '.ajax-pagination-link', function(e) {
    e.preventDefault();
    const $link = $(this);
    const url = $link.attr('href');
    const $container = $link.closest('[data-ajax-grid="true"]');
    
    if (!$container.length) {
        window.location.href = url;
        return;
    }

    $container.addClass('opacity-50');
    
    fetch(url, {
        headers: {
            'X-Requested-With': 'XMLHttpRequest'
        }
    })
    .then(response => response.text())
    .then(html => {
        $container.html(html);
        $container.removeClass('opacity-50');
        
        // Re-init components
        $container.find('.datatable-premium').each(function() {
            initPremiumDataTable(this, {
                columnSearch: $(this).data('column-search') !== false
            });
        });
    })
    .catch(err => {
        console.error('Grid reload failed:', err);
        window.location.href = url;
    });
});
