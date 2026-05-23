using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    public class Entity
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public class TicketCategory
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
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
