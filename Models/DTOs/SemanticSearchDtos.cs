namespace AISupportAnalysisPlatform.Models.DTOs
{
    public class SemanticSearchResponseDto
    {
        public int PoolSize { get; set; }
        public string? ScopeLabel { get; set; }
        public List<SemanticSearchDataDto> Data { get; set; } = new();
    }

    public class SemanticSearchDataDto
    {
        public int Id { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    public class BootstrapEmbeddingsDto
    {
        public string Message { get; set; } = string.Empty;
        public int TotalPoolSize { get; set; }
    }

    public class DebugSemanticDto
    {
        public string AiProviderDbSetting { get; set; } = string.Empty;
        public int TotalEmbeddingsInDb { get; set; }
        public List<VectorDetailDto> VectorDetails { get; set; } = new();
    }

    public class VectorDetailDto
    {
        public int TicketId { get; set; }
        public string? ModelName { get; set; }
        public int VectorLength { get; set; }
        public bool IsEmpty { get; set; }
        public string FirstThreeVals { get; set; } = string.Empty;
    }
}
