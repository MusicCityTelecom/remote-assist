using Microsoft.Web.WebView2.Core;

namespace RemoteAssist.Technician;

internal static class BrowserRuntime
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _environmentTask;

    public static Task<CoreWebView2Environment> GetAsync()
    {
        lock (Gate)
        {
            _environmentTask ??= CreateAsync();
            return _environmentTask;
        }
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remote Assist",
            "SupportTechnician",
            "WebView2");

        Directory.CreateDirectory(userData);

        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData);
    }
}
