using System;
using System.Collections.Generic;

namespace AISupportAnalysisPlatform.Models.Common
{
    public class GridRequestModel
    {
        public const int DefaultPageSize = 10;
        public const int MaxPageSize = 100;
        public const int FullDataPageSize = -1;

        public string? Filter { get; set; }
        public string? SearchString { get; set; }
        public int? StatusId { get; set; }
        public int? PriorityId { get; set; }
        public int? EntityId { get; set; }
        public int? CategoryId { get; set; }
        public int? SourceId { get; set; }
        public string? SortOrder { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = DefaultPageSize;

        // Helper to check if any filters are active
        public bool HasFilters => !string.IsNullOrEmpty(Filter) || 
                                 !string.IsNullOrEmpty(SearchString) || 
                                 StatusId.HasValue || 
                                 PriorityId.HasValue || 
                                 EntityId.HasValue || 
                                 CategoryId.HasValue || 
                                 SourceId.HasValue;

        public void Normalize()
        {
            PageNumber = Math.Max(1, PageNumber);

            if (PageSize == FullDataPageSize)
            {
                return;
            }

            if (PageSize <= 0 || PageSize > MaxPageSize)
            {
                PageSize = DefaultPageSize;
            }
        }

        public int GetEffectivePageSize(int totalCount)
        {
            Normalize();

            if (PageSize == FullDataPageSize)
            {
                return totalCount > 0 ? totalCount : DefaultPageSize;
            }

            return PageSize;
        }
    }

    public interface IPagedResult
    {
        int TotalCount { get; }
        int PageNumber { get; }
        int PageSize { get; }
        int TotalPages { get; }
        bool HasPreviousPage { get; }
        bool HasNextPage { get; }
        GridRequestModel Request { get; }
    }

    public class PagedResult<T> : IPagedResult
    {
        public List<T> Items { get; set; } = new List<T>();
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;
        
        // Context for the grid (e.g. current filters)
        public GridRequestModel Request { get; set; } = new GridRequestModel();
    }
}
