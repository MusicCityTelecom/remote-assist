namespace RemoteAssist.Technician;

internal static class Program
{
    internal const string DefaultServer = "https://support.example.com";
    private const string DeepLinkScheme = "remote-assist-tech";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var server = DefaultServer;
        string? startUrl = null;
        string? deepLink = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
                server = args[++i].TrimEnd('/');
            else if (args[i] == "--url" && i + 1 < args.Length)
                startUrl = args[++i];
            else if (args[i].StartsWith(
                         DeepLinkScheme + "://",
                         StringComparison.OrdinalIgnoreCase))
                deepLink = args[i];
        }

        if (!string.IsNullOrWhiteSpace(deepLink))
            startUrl = ResolveDeepLink(server, deepLink);

        startUrl = EnsureNativeMode(server, startUrl);
        Application.Run(new TechnicianForm(server, startUrl));
    }

    private static string? ResolveDeepLink(string server, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(
                uri.Scheme,
                DeepLinkScheme,
                StringComparison.OrdinalIgnoreCase))
            return null;

        if (!string.Equals(
                uri.Host,
                "session",
                StringComparison.OrdinalIgnoreCase))
            return null;

        var raw = uri.AbsolutePath.Trim('/');
        if (!Guid.TryParse(raw, out var sessionId))
            return null;

        return server.TrimEnd('/') +
               "/?native=1&viewer=" +
               Uri.EscapeDataString(sessionId.ToString());
    }

    private static string EnsureNativeMode(string server, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return server.TrimEnd('/') + "/?native=1";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return server.TrimEnd('/') + "/?native=1";

        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        if (!query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x.StartsWith("native=", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Query = string.IsNullOrWhiteSpace(query)
                ? "native=1"
                : query + "&native=1";
        }

        return builder.Uri.ToString();
    }
}
