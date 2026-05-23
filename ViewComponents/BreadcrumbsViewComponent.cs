using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace AISupportAnalysisPlatform.ViewComponents
{
    public class BreadcrumbsViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke()
        {
            var routeData = ViewContext.RouteData.Values;
            string? controller = routeData["controller"]?.ToString();
            string? action = routeData["action"]?.ToString();
            string? area = routeData["area"]?.ToString();
            string? page = routeData["page"]?.ToString();

            var breadcrumbs = new List<BreadcrumbItem>
            {
                new BreadcrumbItem { Title = "Dashboard", Url = Url.Action("Index", "Dashboard"), Icon = "bi-house-door" }
            };

            if (controller != "Dashboard" || !string.IsNullOrEmpty(area))
            {
                if (!string.IsNullOrEmpty(controller))
                {
                    string parentAction = "Index";
                    string parentTitle = controller;
                    string parentController = controller;

                    if (controller == "AiAnalysis")
                    {
                        parentAction = "Hub";
                        parentTitle = "AnalysisHub";
                    }
                    else if (controller == "AiInsights" || controller == "CopilotAnalysis")
                    {
                        // Group AI sub-tools under the Analysis Hub
                        parentController = "AiAnalysis";
                        parentAction = "Hub";
                        parentTitle = "AnalysisHub";
                    }

                    breadcrumbs.Add(new BreadcrumbItem
                    {
                        Title = parentTitle,
                        Url = Url.Action(parentAction, parentController)
                    });

                    if (parentController != controller || (action != parentAction && action != "Index" && !string.IsNullOrEmpty(action)))
                    {
                        var leafTitle = (parentController != controller && (string.IsNullOrEmpty(action) || action == "Index"))
                            ? controller
                            : action;
                        if (!string.IsNullOrEmpty(leafTitle))
                        {
                            breadcrumbs.Add(new BreadcrumbItem
                            {
                                Title = leafTitle,
                                Url = null
                            });
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(page))
                {
                    // Handle Identity Pages (e.g., /Account/Login)
                    var segments = page.TrimStart('/').Split('/');
                    foreach (var segment in segments)
                    {
                        if (!string.IsNullOrEmpty(segment) && !segment.Equals("Account", StringComparison.OrdinalIgnoreCase))
                        {
                            breadcrumbs.Add(new BreadcrumbItem 
                            { 
                                Title = segment, 
                                Url = null
                            });
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(area) && string.IsNullOrEmpty(controller))
                {
                    breadcrumbs.Add(new BreadcrumbItem { Title = area, Url = null });
                }
            }

            return View(breadcrumbs);
        }
    }

    public class BreadcrumbItem
    {
        public string Title { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? Icon { get; set; }
    }
}
