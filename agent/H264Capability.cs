using Vortice.MediaFoundation;

namespace RemoteAssist.Agent;

internal sealed record H264CapabilityInfo(
    bool HardwareAvailable,
    string[] HardwareEncoders,
    string? Error);

internal static class H264Capability
{
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    public static H264CapabilityInfo Probe()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return new H264CapabilityInfo(false, [], "Windows 10 or newer is required.");

        var started = false;
        try
        {
            var startup = MediaFactory.MFStartup();
            if (startup.Failure)
                return new H264CapabilityInfo(false, [], $"Media Foundation startup failed: 0x{startup.Code:X8}");

            started = true;

            var outputType = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };

            using var encoders = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                null,
                outputType);

            var names = new List<string>();
            foreach (var encoder in encoders)
            {
                string name;
                try
                {
                    name = encoder.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                }
                catch
                {
                    name = "Hardware H.264 encoder";
                }

                if (string.IsNullOrWhiteSpace(name))
                    name = "Hardware H.264 encoder";

                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return new H264CapabilityInfo(names.Count > 0, [.. names], null);
        }
        catch (Exception ex)
        {
            return new H264CapabilityInfo(false, [], ex.Message);
        }
        finally
        {
            if (started)
            {
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
    }
}
