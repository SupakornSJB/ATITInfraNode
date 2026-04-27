using IoTGame.Core.Abstractions;
using IoTGame.Core.Models;

namespace IoTGame.Core.Auth;

public class ApiKeyAdminAuthenticator : IAdminAuthenticator
{
    private readonly string? _apiKey;

    public ApiKeyAdminAuthenticator(IoTGameOptions options)
    {
        _apiKey = options.AdminApiKey;
    }

    public Task<bool> IsAuthorizedAsync(HttpContext context, string action)
    {
        if (string.IsNullOrEmpty(_apiKey)) return Task.FromResult(true);

        var provided = context.Request.Headers["X-IoTGame-Key"].FirstOrDefault();
        return Task.FromResult(provided == _apiKey);
    }
}
