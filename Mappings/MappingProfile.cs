using AutoMapper;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Models.DTOs;
using AISupportAnalysisPlatform.Models.Common;
using AISupportAnalysisPlatform.Services.AI;

namespace AISupportAnalysisPlatform.Mappings
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            // Identity
            CreateMap<ApplicationUser, LookupDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.FullName));

            // Reference Data
            CreateMap<TicketStatus, LookupDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id.ToString()))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name));

            CreateMap<TicketPriority, LookupDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id.ToString()))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name));

            // AI Analysis
            CreateMap<TicketAiAnalysis, TicketAiAnalysisDto>()
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.AnalysisStatus))
                .ForMember(dest => dest.Confidence, opt => opt.MapFrom(src => src.ConfidenceLevel))
                .ForMember(dest => dest.Model, opt => opt.MapFrom(src => src.ModelName))
                .ForMember(dest => dest.Duration, opt => opt.MapFrom(src => src.ProcessingDurationMs))
                .ForMember(dest => dest.PromptSize, opt => opt.MapFrom(src => src.InputPromptSize))
                .ForMember(dest => dest.Metadata, opt => opt.MapFrom(src => src.DiagnosticMetadata))
                .ForMember(dest => dest.CreatedOn, opt => opt.MapFrom(src => src.CreatedOn.ToString("yyyy-MM-dd HH:mm")))
                .ForMember(dest => dest.LastRefreshed, opt => opt.MapFrom(src => (src.LastRefreshedOn ?? src.CreatedOn).ToString("yyyy-MM-dd HH:mm")));

            CreateMap<TicketAiAnalysis, TicketRunHistoryDto>()
                .ForMember(dest => dest.AnalysisStatus, opt => opt.MapFrom(src => src.AnalysisStatus.ToString()))
                .ForMember(dest => dest.ConfidenceLevel, opt => opt.MapFrom(src => src.ConfidenceLevel.ToString()))
                .ForMember(dest => dest.Duration, opt => opt.MapFrom(src => src.ProcessingDurationMs))
                .ForMember(dest => dest.CreatedOn, opt => opt.MapFrom(src => src.CreatedOn.ToString("yyyy-MM-dd HH:mm")));

            CreateMap<TicketAiAnalysisLog, AiLogDto>()
                .ForMember(dest => dest.Time, opt => opt.MapFrom(src => src.CreatedOn.ToString("HH:mm:ss")))
                .ForMember(dest => dest.Level, opt => opt.MapFrom(src => src.LogLevel.ToString()));

            CreateMap<CopilotTicketRecommendation, AiRecommendationDto>()
                .ForMember(dest => dest.GeneratedOn, opt => opt.MapFrom(src => src.GeneratedOnUtc.ToString("yyyy-MM-dd HH:mm")));

            CreateMap<CopilotTicketCitation, AiSearchMatchDto>()
                .ForMember(dest => dest.ResolvedAt, opt => opt.Ignore());

            CreateMap<KnowledgeBaseChunkMatch, KnowledgeMatchDto>();

            // Management
            CreateMap<Entity, EntityDto>();
            CreateMap<ApplicationUser, UserDto>()
                .ForMember(dest => dest.EntityName, opt => opt.MapFrom(src => src.Entity != null ? src.Entity.Name : null));

            CreateMap<Ticket, LookupDisplayDto>()
                .ForMember(dest => dest.Display, opt => opt.MapFrom(src => src.TicketNumber + " - " + src.Title));

            CreateMap<TicketSemanticEmbedding, VectorDetailDto>()
                .ForMember(dest => dest.VectorLength, opt => opt.MapFrom(src => src.Vector.Length))
                .ForMember(dest => dest.IsEmpty, opt => opt.MapFrom(src => src.Vector == null || src.Vector.Length == 0))
                .ForMember(dest => dest.FirstThreeVals, opt => opt.MapFrom(src => src.Vector != null && src.Vector.Length >= 3 ? $"[{src.Vector[0]:F4}, {src.Vector[1]:F4}, {src.Vector[2]:F4}...]" : "[]"));

            
            CreateMap<SemanticSearchMatch, AiSearchMatchDto>()
                .ForMember(dest => dest.TicketId, opt => opt.MapFrom(src => src.Ticket.Id))
                .ForMember(dest => dest.TicketNumber, opt => opt.MapFrom(src => src.Ticket.TicketNumber))
                .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Ticket.Title))
                .ForMember(dest => dest.ResolutionSummary, opt => opt.MapFrom(src => src.Ticket.ResolutionSummary ?? ""))
                .ForMember(dest => dest.RootCause, opt => opt.MapFrom(src => src.Ticket.RootCause ?? ""))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Ticket.Status != null ? src.Ticket.Status.Name : ""))
                .ForMember(dest => dest.Score, opt => opt.MapFrom(src => src.Score))
                .ForMember(dest => dest.ResolvedAt, opt => opt.MapFrom(src => src.Ticket.ResolvedAt != null ? src.Ticket.ResolvedAt.Value.ToString("yyyy-MM-dd") : null));

            CreateMap<IGrouping<string, Ticket>, DashboardMetricItem>()
                .ForMember(dest => dest.Label, opt => opt.MapFrom(src => src.Key))
                .ForMember(dest => dest.Value, opt => opt.MapFrom(src => src.Count()));

            CreateMap<SemanticSearchMatch, CopilotTicketCitation>()
                .ForMember(dest => dest.TicketId, opt => opt.MapFrom(src => src.Ticket.Id))
                .ForMember(dest => dest.TicketNumber, opt => opt.MapFrom(src => src.Ticket.TicketNumber))
                .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Ticket.Title))
                .ForMember(dest => dest.ResolutionSummary, opt => opt.MapFrom(src => src.Ticket.ResolutionSummary ?? ""))
                .ForMember(dest => dest.RootCause, opt => opt.MapFrom(src => src.Ticket.RootCause ?? ""))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Ticket.Status != null ? src.Ticket.Status.Name : ""))
                .ForMember(dest => dest.Score, opt => opt.MapFrom(src => src.Score));

            CreateMap<TicketCategory, ReferenceDataDto>();
            CreateMap<TicketPriority, ReferenceDataDto>();
            CreateMap<TicketStatus, ReferenceDataDto>();
            CreateMap<TicketSource, ReferenceDataDto>();

            CreateMap<TicketAiAnalysis, AnalysisSnapshot>()
                .ForMember(dest => dest.AnalysisId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.TicketNumber, opt => opt.MapFrom(src => src.Ticket != null ? src.Ticket.TicketNumber : "Unknown"))
                .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Ticket != null ? src.Ticket.Title : "Unknown"))
                .ForMember(dest => dest.CurrentClassification, opt => opt.MapFrom(src => src.Ticket != null && src.Ticket.Category != null ? src.Ticket.Category.Name : "None"))
                .ForMember(dest => dest.CurrentPriority, opt => opt.MapFrom(src => src.Ticket != null && src.Ticket.Priority != null ? src.Ticket.Priority.Name : "Unknown"))
                .ForMember(dest => dest.CurrentPriorityLevel, opt => opt.MapFrom(src => src.Ticket != null && src.Ticket.Priority != null ? src.Ticket.Priority.Level : 0))
                .ForMember(dest => dest.DescriptionLength, opt => opt.MapFrom(src => src.Ticket != null && src.Ticket.Description != null ? src.Ticket.Description.Length : 0))
                .ForMember(dest => dest.TicketAttachmentCount, opt => opt.MapFrom(src => src.Ticket != null ? src.Ticket.Attachments.Count : 0));

            CreateMap<Notification, NotificationDto>()
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt.ToString("yyyy-MM-dd HH:mm")));

            CreateMap<CopilotToolDefinition, CopilotToolDto>()
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Title))
                .ForMember(dest => dest.LastRunAt, opt => opt.MapFrom(src => src.UpdatedAt != null ? src.UpdatedAt.Value.ToString("yyyy-MM-dd HH:mm") : null));

            CreateMap<Ticket, TicketDto>()
                .ForMember(dest => dest.EntityName, opt => opt.MapFrom(src => src.Entity != null ? src.Entity.Name : null))
                .ForMember(dest => dest.CategoryName, opt => opt.MapFrom(src => src.Category != null ? src.Category.Name : null))
                .ForMember(dest => dest.PriorityName, opt => opt.MapFrom(src => src.Priority != null ? src.Priority.Name : null))
                .ForMember(dest => dest.StatusName, opt => opt.MapFrom(src => src.Status != null ? src.Status.Name : null))
                .ForMember(dest => dest.IsClosedState, opt => opt.MapFrom(src => src.Status != null && src.Status.IsClosedState))
                .ForMember(dest => dest.CreatedByUserName, opt => opt.MapFrom(src => src.CreatedByUser != null ? (src.CreatedByUser.FirstName + " " + src.CreatedByUser.LastName) : null))
                .ForMember(dest => dest.AssignedToUserName, opt => opt.MapFrom(src => src.AssignedToUser != null ? (src.AssignedToUser.FirstName + " " + src.AssignedToUser.LastName) : null))
                .ForMember(dest => dest.AttachmentCount, opt => opt.MapFrom(src => src.Attachments.Count))
                .ForMember(dest => dest.CommentCount, opt => opt.MapFrom(src => src.Comments.Count));

            CreateMap<Ticket, CopilotEvaluationTicketItem>()
                .ForMember(dest => dest.TicketId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Status != null ? src.Status.Name : ""));

            CreateMap<SemanticSearchMatch, SemanticSearchDataDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Ticket.Id))
                .ForMember(dest => dest.TicketNumber, opt => opt.MapFrom(src => src.Ticket.TicketNumber))
                .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Ticket.Title))
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.Ticket.CreatedAt.ToString("MMM dd, yyyy")))
                .ForMember(dest => dest.Score, opt => opt.MapFrom(src => Math.Round(src.Score * 100, 1)));

            CreateMap<RetrievalBenchmarkValidationResult, BenchmarkValidationDto>()
                .ForMember(dest => dest.IsValid, opt => opt.MapFrom(src => src.Cases.All(c => !c.IsSourceTicketMissing && c.MissingExpectedTicketIds.Count == 0)))
                .ForMember(dest => dest.Errors, opt => opt.MapFrom(src => src.Cases.SelectMany(c => c.Warnings).ToList()));

            CreateMap<BilingualRetrievalBenchmarkRunResult, RetrievalBenchmarkResultDto>()
                .ForMember(dest => dest.HitRate, opt => opt.MapFrom(src => src.TotalCases > 0 ? (double)src.HitCases / src.TotalCases * 100 : 0))
                .ForMember(dest => dest.Results, opt => opt.MapFrom(src => src.Cases));

            CreateMap<BatchQueueProgress, BatchProgressDto>()
                .ForMember(dest => dest.QueueLength, opt => opt.Ignore());
        }
    }
}
