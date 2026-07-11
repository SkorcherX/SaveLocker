using SaveLocker.Server.Services;

namespace SaveLocker.Server;

/// <summary>
/// Endpoint filter that guards the admin dashboard API.
/// Passes through freely when no password has been configured yet (first-run open state).
/// Once a password is set, every request must supply it in the X-Admin-Password header.
/// </summary>
public sealed class AdminPasswordFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var settings = http.RequestServices.GetRequiredService<SettingsService>();

        var storedHash = await settings.GetEffectiveAsync(SettingsService.AdminPasswordHash);
        if (string.IsNullOrEmpty(storedHash))
            return await next(ctx);

        var provided = http.Request.Headers["X-Admin-Password"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !Tokens.VerifyPassword(provided, storedHash))
            return Results.Unauthorized();

        return await next(ctx);
    }
}
