namespace IoTGame.Core.Abstractions;

public interface IAdminAuthenticator
{
    Task<bool> IsAuthorizedAsync(HttpContext context, string action);
}
