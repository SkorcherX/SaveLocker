using SaveLocker.Server.Data;
using SaveLocker.Server.Services;

namespace SaveLocker.Server;

/// <summary>
/// Endpoint filter that authenticates a request by its <c>X-Api-Key</c> header,
/// resolves the calling <see cref="Machine"/>, and stashes it in HttpContext.Items.
/// Applied to the authenticated route group.
/// </summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string MachineItemKey = "machine";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var apiKey = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
            return Results.Unauthorized();

        var sync = http.RequestServices.GetRequiredService<SyncService>();
        var machine = await sync.AuthenticateAsync(apiKey);
        if (machine is null)
            return Results.Unauthorized();

        http.Items[MachineItemKey] = machine;
        return await next(ctx);
    }
}

public static class HttpContextExtensions
{
    public static Machine CurrentMachine(this HttpContext http) =>
        (Machine)http.Items[ApiKeyFilter.MachineItemKey]!;
}
