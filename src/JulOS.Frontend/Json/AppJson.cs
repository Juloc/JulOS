using System.Text.Json.Serialization;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Files;
using JulOS.Frontend.SystemInfo;

namespace JulOS.Frontend;

/// <summary>JSON ohne Reflection: alle Typen, die die API sendet oder empfaengt, stehen hier.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(SetupBeginRequest))]
[JsonSerializable(typeof(SetupBeginResponse))]
[JsonSerializable(typeof(SetupFinishRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(SessionsResponse))]
[JsonSerializable(typeof(EndedResponse))]
[JsonSerializable(typeof(ActivateRequest))]
[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(ModeRequest))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(ConnectionDto))]
[JsonSerializable(typeof(ConnectionsResponse))]
[JsonSerializable(typeof(ConnectionRequest))]
[JsonSerializable(typeof(TicketRequest))]
[JsonSerializable(typeof(TicketResponse))]
[JsonSerializable(typeof(OverviewResponse))]
[JsonSerializable(typeof(FileListingDto))]
[JsonSerializable(typeof(FileSearchDto))]
internal sealed partial class AppJson : JsonSerializerContext;
