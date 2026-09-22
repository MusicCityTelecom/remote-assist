using System.Security.Principal;

namespace RemoteAssist.Agent;

internal static class Program
{
    internal const string DefaultServer = "https://support.example.com";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = StartupOptions.Parse(args);
        Application.Run(new MainForm(options));
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

internal sealed record StartupOptions(string Server, string? Code, string? ResumeFile)
{
    public static StartupOptions Parse(string[] args)
    {
        var server = Environment.GetEnvironmentVariable("REMOTE_ASSIST_SUPPORT_URL") ?? Program.DefaultServer;
        string? code = null;
        string? resumeFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            else if (args[i] == "--code" && i + 1 < args.Length) code = args[++i];
            else if (args[i] == "--resume-file" && i + 1 < args.Length) resumeFile = args[++i];
        }
        return new StartupOptions(server.TrimEnd('/'), code, resumeFile);
    }
}
