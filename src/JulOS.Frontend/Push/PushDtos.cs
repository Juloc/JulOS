using JulOS.Backend.Logic;

namespace JulOS.Frontend.Push;

internal sealed record PushDeviceDto(Guid Id, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastOk);
internal sealed record AlertLogDto(long Id, DateTimeOffset At, Guid? HostId, string Host, string Rule, string Kind, string? Detail, string Push);
/// <summary>Available: der Server kann Schluessel verschluesseln (sonst gibt es keine Push-Meldungen).</summary>
internal sealed record PushStateResponse(bool Available, int MaxDevices, PushDeviceDto[] Devices, AlertSettings Settings, AlertLogDto[] Log);
internal sealed record PushKeyResponse(string PublicKey);
internal sealed record PushKeys(string P256dh, string Auth);
internal sealed record PushSubscribeRequest(string Endpoint, PushKeys Keys, string? Label);
internal sealed record PushSubscribeResponse(Guid Id, bool Created);
internal sealed record PushTestResponse(int Sent, int Failed, int Removed, int Devices, bool Limited);
