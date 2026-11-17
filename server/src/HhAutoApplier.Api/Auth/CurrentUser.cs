using System.Security.Claims;
using HhAutoApplier.Application.Abstractions;

namespace HhAutoApplier.Api.Auth;

// Пользователь запроса по cookie или ключу API; вне запроса — пользователь чата Telegram (ActingUser), если бот его задал
public class CurrentUser(IHttpContextAccessor accessor, ActingUser acting) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? Id => acting.User is { } user ? user.Id
        : Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string Name => acting.User?.Email ?? Principal?.Identity?.Name ?? "";

    public string Role => acting.User?.Role ?? Principal?.FindFirstValue(ClaimTypes.Role) ?? "";

    public bool ViaApiKey => acting.User is null && Principal?.FindFirstValue(AuthClaims.Method) == ApiKeyAuthenticationHandler.Name;
}
