using System.Text.Json.Serialization;
using JulOS.Frontend.Auth;

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
internal sealed partial class AppJson : JsonSerializerContext;
