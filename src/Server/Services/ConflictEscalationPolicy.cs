using SaveLocker.Server.Data;

namespace SaveLocker.Server.Services;

public sealed class ConflictEscalationPolicy
{
    public TimeSpan After { get; }

    public ConflictEscalationPolicy(IConfiguration config)
    {
        var seconds = config.GetValue<double?>("Conflicts:EscalationAfterSeconds")
                      ?? TimeSpan.FromHours(6).TotalSeconds;
        After = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    public bool IsEscalated(ConflictFlag conflict, DateTime now) =>
        conflict.Status == SaveLocker.Shared.ConflictStatus.Open &&
        conflict.CreatedAt <= now - After;
}
