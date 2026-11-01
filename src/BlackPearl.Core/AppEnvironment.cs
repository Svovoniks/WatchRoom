namespace BlackPearl.Core;

/// <summary>
/// Environment settings are named BLACKPEARL_*. The app was called Watchroom before, so the WATCHROOM_* name of the same
/// setting still applies when the new one is not set, which keeps existing shortcuts, scripts and server configuration working.
/// </summary>
public static class AppEnvironment
{
    public const string Prefix = "BLACKPEARL_", FormerPrefix = "WATCHROOM_";
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(Prefix + name) is { Length: > 0 } value ? value : Environment.GetEnvironmentVariable(FormerPrefix + name);
}
