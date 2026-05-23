namespace AISupportAnalysisPlatform.Models.DTOs
{
    public class LookupDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class LookupDisplayDto
    {
        public int Id { get; set; }
        public string Display { get; set; } = string.Empty;
    }

    public class AiModelDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Size { get; set; }
        public string? ParameterSize { get; set; }
        public string? Family { get; set; }
        public string? Quantization { get; set; }
        public string? ContextWindow { get; set; }
        public bool IsActive { get; set; }
    }

    public class DockerModelDto
    {
        public string Name { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string ParameterSize { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string Quantization { get; set; } = string.Empty;
    }

    public class DockerModelsResponseDto
    {
        public List<AiModelDto> Models { get; set; } = new();
        public string? ActiveModel { get; set; }
    }

    public class DockerStatusDto
    {
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
