using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    public class ApplicationUser : IdentityUser
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string LastName { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public int? EntityId { get; set; }
        public Entity? Entity { get; set; }

        public string FullName => $"{FirstName} {LastName}";
    }
}
