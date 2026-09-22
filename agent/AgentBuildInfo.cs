using System.Reflection;

namespace RemoteAssist.Agent;

internal static class AgentBuildInfo
{
    private static readonly Assembly Current = typeof(AgentBuildInfo).Assembly;

    public static string Version { get; } =
        Current.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion { get; } =
        Current.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Version;
}
