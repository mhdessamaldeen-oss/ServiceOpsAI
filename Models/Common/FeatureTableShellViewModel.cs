using Microsoft.AspNetCore.Html;

namespace AISupportAnalysisPlatform.Models.Common;

public class FeatureTableShellViewModel
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }

    public string? HeaderActionText { get; set; }
    public string? HeaderActionHref { get; set; }
    public string? HeaderActionIconClass { get; set; }
    public string HeaderActionCssClass { get; set; } = "btn btn-sm fw-bold px-3 py-2 rounded-pill";
    public string? HeaderActionStyle { get; set; }

    public bool ShowColumnSearchToggle { get; set; }
    public string ColumnSearchLabel { get; set; } = "In-Depth Column Filters";
    public string? TableSelector { get; set; }

    public string? ToolbarChipText { get; set; }
    public string? ToolbarChipIconClass { get; set; }
    public string? ToolbarChipStyle { get; set; }
    public string? ToolbarChipHtmlId { get; set; }

    public string? ToolbarActionText { get; set; }
    public string? ToolbarActionHref { get; set; }
    public string ToolbarActionCssClass { get; set; } = "btn btn-sm btn-outline-primary fw-bold rounded-pill";
    public string? ToolbarActionStyle { get; set; }

    public string ContainerCssClass { get; set; } = "feature-table-shell shadow-lg mb-5";
    public string TableBodyCssClass { get; set; } = "feature-table-body p-3";

    public string TablePartialName { get; set; } = string.Empty;
    public object? TableModel { get; set; }

    public string? FooterPartialName { get; set; }
    public object? FooterModel { get; set; }
    public IPagedResult? PaginationModel { get; set; }

    public bool HasHeaderAction => !string.IsNullOrWhiteSpace(HeaderActionText) && !string.IsNullOrWhiteSpace(HeaderActionHref);
    public bool HasToolbarAction => !string.IsNullOrWhiteSpace(ToolbarActionText) && !string.IsNullOrWhiteSpace(ToolbarActionHref);
    public bool HasToolbarChip => !string.IsNullOrWhiteSpace(ToolbarChipText);
    public bool HasToolbar => ShowColumnSearchToggle || HasToolbarAction || HasToolbarChip;
}
