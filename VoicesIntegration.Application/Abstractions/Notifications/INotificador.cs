namespace VoicesIntegration.Application.Abstractions.Notifications;

public interface INotificador
{
    Task EnviarAsync(string asunto, string cuerpo, CancellationToken cancellationToken);

    Task<string?> ProbarAsync(CancellationToken cancellationToken);
}
