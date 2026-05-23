using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AISupportAnalysisPlatform.Models
{
    /// <summary>
    /// Stores the high-dimensional vector representation of a ticket's title and description.
    /// Used for semantic similarity search.
    /// </summary>
    public class TicketSemanticEmbedding
    {
        [Key]
        [ForeignKey("Ticket")]
        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }

        /// <summary>
        /// The raw embedding vector.
        /// Stored as a JSON string in the DB since SQL Server lacks a native vector type.
        /// </summary>
        [Required]
        public string VectorJson { get; set; } = string.Empty;

        /// <summary>
        /// The model name used to generate this embedding. 
        /// Crucial because vectors from different models are not comparable.
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ModelName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Helper to get/set as float array
        [NotMapped]
        public float[] Vector
        {
            get => System.Text.Json.JsonSerializer.Deserialize<float[]>(VectorJson) ?? Array.Empty<float>();
            set => VectorJson = System.Text.Json.JsonSerializer.Serialize(value);
        }
    }
}
