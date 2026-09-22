using Vortice.MediaFoundation;

namespace RemoteAssist.Agent;

internal static class MediaFoundationRuntime
{
    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        if (_started) return;

        lock (Gate)
        {
            if (_started) return;
            MediaFactory.MFStartup(false).CheckError();
            _started = true;
        }
    }
}
