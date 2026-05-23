using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models
{
    public class TicketCategory
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string NameAr { get; set; } = string.Empty;

        // Hierarchy: ParentCategoryId = null for top-level "Electricity" / "Internet" /
        // "Water" / "Gas" / "Government Process" parents. Children (e.g. "Service down",
        // "Pipe burst") point at their parent.
        public int? ParentCategoryId { get; set; }
        public TicketCategory? ParentCategory { get; set; }
        public ICollection<TicketCategory> Children { get; set; } = new List<TicketCategory>();

        // Tier classifies the category in the taxonomy:
        //   Primary   = canonical top-level service group (Electricity, Internet, ...)
        //   Secondary = specific issue under a Primary parent
        //   Temporary = ad-hoc addition that didn't fit the taxonomy yet — admin to clean up
        public CategoryTier Tier { get; set; } = CategoryTier.Secondary;

        // Optional link to the service this category relates to — lets the Copilot answer
        // "complaints about electricity" without walking the parent name; also useful for
        // routing tickets to the matching Department.
        public int? ServiceTypeId { get; set; }
        public ServiceType? ServiceType { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public enum CategoryTier
    {
        Primary   = 1,
        Secondary = 2,
        Temporary = 3
    }

    public class TicketPriority
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public int Level { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class TicketStatus
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
        public bool IsClosedState { get; set; } = false;
    }

    public class TicketSource
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public class SystemSetting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Key { get; set; } = string.Empty;

        [Required]
        public string Value { get; set; } = string.Empty;
    }

    public class CustomTheme
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(10)]
        public string PrimaryColor { get; set; } = "#10b981";

        [Required]
        [StringLength(10)]
        public string BgMain { get; set; } = "#0a0a0a";

        [Required]
        [StringLength(10)]
        public string BgCard { get; set; } = "#18181b";

        [Required]
        [StringLength(10)]
        public string BgSidebar { get; set; } = "#000000";

        [Required]
        [StringLength(10)]
        public string BgHeader { get; set; } = "#000000";

        [Required]
        [StringLength(10)]
        public string TextMain { get; set; } = "#ffffff";

        [Required]
        [StringLength(10)]
        public string TextMuted { get; set; } = "#d1d5db";

        [Required]
        [StringLength(10)]
        public string BorderColor { get; set; } = "#27272a";

        // Extended tokens (nullable — Layout falls back to computed defaults if a theme leaves them blank)

        /// <summary>Text color used on top of the Primary surface (white for dark primaries, black for light primaries).</summary>
        [StringLength(10)]
        public string? PrimaryContrastText { get; set; }

        /// <summary>Secondary surface used for inputs, sub-cards, table-row stripes — slightly differs from BgCard.</summary>
        [StringLength(10)]
        public string? BgSurfaceAlt { get; set; }

        /// <summary>Elevated surface for dropdowns, modals, popovers.</summary>
        [StringLength(10)]
        public string? BgElevated { get; set; }

        [StringLength(10)]
        public string? SuccessColor { get; set; }

        [StringLength(10)]
        public string? WarningColor { get; set; }

        [StringLength(10)]
        public string? DangerColor { get; set; }

        [StringLength(10)]
        public string? InfoColor { get; set; }

        /// <summary>Stored as an "rgba(r, g, b, a)" or "#rrggbb" string. Used for box-shadows.</summary>
        [StringLength(40)]
        public string? ShadowColor { get; set; }

        public bool IsSystemTheme { get; set; } = false;
        public string? SystemIdentifier { get; set; }
    }

    public class ExternalApiSetting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Endpoint { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public string? Description { get; set; }
    }
}
