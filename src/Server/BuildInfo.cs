using System.Reflection;
using System.Globalization;
using SaveLocker.Shared;

namespace SaveLocker.Server;

/// <summary>
/// Resolves what this build is, once, at startup.
/// </summary>
/// <remarks>
/// The version cannot come from MinVer here the way it does for the agent: the Docker build has no
/// git history to walk, and MinVer fails SILENTLY to 0.0.0.0 rather than erroring (see Gotchas.md —
/// release.yml already works around the same trap by passing --property:Version explicitly). So the
/// workflow computes the version and bakes it in as environment variables.
///
/// Three sources, in descending order of trust:
///   1. SAVELOCKER_VERSION / _COMMIT / _BUILT_AT — set by the Dockerfile from build args. Real deploys.
///   2. The assembly's InformationalVersion — set if someone published with -p:Version.
///   3. "dev" — a local `dotnet run`. Deliberately NOT a plausible-looking number: an unstamped
///      build that claims a version is worse than one that admits it has none.
/// </remarks>
public static class BuildInfo
{
    public static ServerBuildInfo Current { get; } = Resolve();

    private static ServerBuildInfo Resolve()
    {
        var version = Env("SAVELOCKER_VERSION")
            ?? Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";

        // The SDK appends "+{sha}" to InformationalVersion on its own when SourceLink is in play,
        // which would collide with our own "+{n}.{sha}" suffix. Ours always has the commit count,
        // so a bare "+sha" from the SDK is dropped rather than mistaken for it.
        if (Env("SAVELOCKER_VERSION") is null)
        {
            var plus = version.IndexOf('+');
            if (plus > 0 && !version[(plus + 1)..].Contains('.'))
                version = version[..plus];
        }

        var commit = Env("SAVELOCKER_COMMIT") ?? "";

        DateTime? builtAt = null;
        if (Env("SAVELOCKER_BUILT_AT") is { } raw &&
            DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            builtAt = parsed;

        // A release is a build sitting exactly ON a tag. `git describe --long` always emits the
        // "-{n}-g{sha}" tail, so the workflow converts it to "+{n}.{sha}" only when n > 0 — which
        // makes the absence of a '+' the signal, and keeps this check off git entirely.
        var isRelease = version != "dev" && !version.Contains('+');

        return new ServerBuildInfo(version, commit, builtAt, isRelease);
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
