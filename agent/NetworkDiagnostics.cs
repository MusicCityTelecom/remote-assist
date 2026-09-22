using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace RemoteAssist.Agent;

internal sealed record NetworkAdapterSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("ipv4")] string[] IPv4,
    [property: JsonPropertyName("netmasks")] string[] Netmasks,
    [property: JsonPropertyName("gateways")] string[] Gateways,
    [property: JsonPropertyName("dns_servers")] string[] DnsServers,
    [property: JsonPropertyName("default_route")] bool DefaultRoute);

internal sealed record NetworkSnapshot(
    [property: JsonPropertyName("public_ip")] string PublicIp,
    [property: JsonPropertyName("adapters")] NetworkAdapterSnapshot[] Adapters,
    [property: JsonPropertyName("captured_at")] DateTime CapturedAt);

internal static class NetworkDiagnostics
{
    private const string PublicIpUrl = "https://tools.remote-assistucp.com/ip.php";

    public static async Task<NetworkSnapshot> CaptureAsync(
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var adapters = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(ToSnapshot)
            .Where(x =>
                x.IPv4.Length > 0 ||
                x.Gateways.Length > 0)
            .OrderByDescending(x => x.DefaultRoute)
            .ThenBy(x => MethodOrder(x.Method))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var publicIp = "";
        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var raw = (await http.GetStringAsync(PublicIpUrl, timeout.Token)).Trim();
            if (IPAddress.TryParse(raw, out var parsed))
                publicIp = parsed.ToString();
        }
        catch
        {
            // LAN diagnostics are still useful when the public-IP helper
            // is temporarily unreachable.
        }

        return new NetworkSnapshot(
            publicIp,
            adapters,
            DateTime.UtcNow);
    }

    private static NetworkAdapterSnapshot ToSnapshot(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();

        var ipv4 = new List<string>();
        var masks = new List<string>();

        foreach (var address in properties.UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address.Address))
                continue;

            ipv4.Add(address.Address.ToString());

            var mask = address.IPv4Mask;
            if (mask is not null)
                masks.Add(mask.ToString());
        }

        var gateways = properties.GatewayAddresses
            .Select(x => x.Address)
            .Where(IsUsefulAddress)
            .Select(x => x.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dns = properties.DnsAddresses
            .Where(IsUsefulAddress)
            .Select(x => x.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NetworkAdapterSnapshot(
            adapter.Name,
            adapter.Description,
            Classify(adapter.NetworkInterfaceType),
            ipv4.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            masks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            gateways,
            dns,
            gateways.Length > 0);
    }

    private static bool IsUsefulAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.None);

    private static string Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wireless",
        NetworkInterfaceType.Ethernet => "Wired",
        NetworkInterfaceType.GigabitEthernet => "Wired",
        NetworkInterfaceType.FastEthernetFx => "Wired",
        NetworkInterfaceType.FastEthernetT => "Wired",
        NetworkInterfaceType.Ppp => "VPN / PPP",
        NetworkInterfaceType.Tunnel => "VPN / Tunnel",
        _ => "Other"
    };

    private static int MethodOrder(string method) => method switch
    {
        "Wired" => 0,
        "Wireless" => 1,
        _ => 2
    };
}
