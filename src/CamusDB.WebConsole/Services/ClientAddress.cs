using System.Net;
using System.Net.Sockets;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Turns a caller's socket address into the key the throttles count against.
///
/// <para>Both throttles — the sign-in counter and the launch rate limiter — have to agree on what
/// "the same caller" means, so the mapping lives here rather than in either of them.</para>
/// </summary>
public static class ClientAddress
{
    /// <summary>
    /// Key for one caller. An IPv6 address is cut to its /64 prefix: a single home connection is
    /// routinely given that whole range and can pick a fresh address from it per request, so counting
    /// full IPv6 addresses counts nothing. IPv4 is used whole, /32 being one host already.
    /// </summary>
    public static string Key(IPAddress? address)
    {
        if (address is null)
            return LoginAttemptThrottle.UnknownClient;

        // A v4 address arriving over a dual-stack socket is written ::ffff:a.b.c.d. Left alone it
        // would be cut to a /64 below and merge every IPv4 caller into one bucket.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        byte[] bytes = address.GetAddressBytes();

        for (int i = 8; i < bytes.Length; i++)
            bytes[i] = 0;

        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Key for the caller of an HTTP request. See the note on <see cref="Options.ConsoleSecurityOptions"/>.</summary>
    public static string Key(HttpContext context) => Key(context.Connection.RemoteIpAddress);
}
