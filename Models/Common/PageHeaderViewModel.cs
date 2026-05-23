namespace AISupportAnalysisPlatform.Models.Common;

public class PageHeaderViewModel
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string IconClass { get; set; } = "bi bi-grid-1x2-fill";

    public List<PageHeaderChip> Chips { get; set; } = new();
    public List<PageHeaderAction> Actions { get; set; } = new();

    /// <summary>Optional partial rendered after the title row — used by pages whose toolbar contains JS-bound buttons (Run/Stop/Filter selectors).</summary>
    public string? ToolbarPartialName { get; set; }
    public object? ToolbarModel { get; set; }
}

public class PageHeaderChip
{
    public string Text { get; set; } = string.Empty;
    public string? IconClass { get; set; }
    public string? Style { get; set; }
}

public class PageHeaderAction
{
    public string Text { get; set; } = string.Empty;
    public string Href { get; set; } = "#";
    public string? IconClass { get; set; }
    public string CssClass { get; set; } = "btn btn-sm btn-soft-secondary rounded-pill px-3 fw-bold";
    public string? Title { get; set; }
}
