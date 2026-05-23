namespace AISupportAnalysisPlatform.Services.Notifications;

public interface INotificationService
{
    Task CreateNotificationAsync(string userId, string title, string message, string? link = null);
}
