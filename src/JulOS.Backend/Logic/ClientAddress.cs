using System.Net;
using System.Net.Sockets;

namespace JulOS.Backend.Logic;

/// <summary>
/// Der Schluessel einer Client-Adresse fuer Sperren und Anzeige. IPv4 bleibt die Adresse selbst. Bei IPv6 zaehlt das ganze /64-Netz:
/// ein Anschluss bekommt normalerweise ein /64 und kann darin beliebig viele Adressen wechseln, die Sperre pro Adresse liesse sich so umgehen.
/// (Fruehere Fassung: <c>MapToIPv4</c> bei jeder Adresse, das nimmt bei echtem IPv6 nur die letzten 32 Bit und wuerfelt Netze zusammen.)
/// </summary>
public static class ClientAddress
{
    public static string Key(IPAddress? address)
    {
        if (address is null) return "?";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
