using System;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    public class Notification
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [StringLength(500)]
        public string Message { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsRead { get; set; } = false;

        [Required]
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        [StringLength(255)]
        public string? Link { get; set; }
    }
}
