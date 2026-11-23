using System.Text.Json;
using System.Text.Json.Serialization;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Ai;

// ---- API fuer Apps (/api/ai/v1) ----
internal sealed record AiErrorDto(string Code, string Message, string? Scope = null, DateTimeOffset? ResetsAt = null);
internal sealed record AiErrorBody(AiErrorDto Error);
internal sealed record ChatMessageDto(string? Role, JsonElement Content);
internal sealed record ChatRequestDto(string? Model, ChatMessageDto[]? Messages, string? Effort, int? MaxTokens, bool? Stream);
internal sealed record UsageDto(long InputTokens, long OutputTokens);
internal sealed record BudgetPartDto(long Used, long? Limit);
internal sealed record BudgetDto(BudgetPartDto Day, BudgetPartDto Month);
internal sealed record ChatResponseDto(string Id, string Model, string Text, UsageDto Usage, BudgetDto Budget, string? Warning);
internal sealed record StartEventDto(string Id, string Model);
internal sealed record TextEventDto(string Text);
internal sealed record DoneEventDto(string Id, string Model, UsageDto Usage, BudgetDto Budget, string? Warning);
internal sealed record ModelDto(string Id, string Provider, string Name, string[] Efforts, bool Default);
internal sealed record ModelsResponseDto(ModelDto[] Models, string? Default, string[] Unavailable);
internal sealed record UsageDayDto(string Day, string Model, int Requests, long TokensIn, long TokensOut);
internal sealed record UsageResponseDto(string App, BudgetDto Budget, string? Warning, int RatePerMinute, UsageDayDto[] Recent);

// ---- Verwaltung in den Einstellungen (/api/ai, hinter Sitzung) ----
internal sealed record LoginDto(string State, string? Url, string? Code, DateTimeOffset? ExpiresAt);
internal sealed record CodexDto(string Version, bool Supported, LoginDto Login);
internal sealed record PlanWindowDto(double UsedPercent, int? Minutes, DateTimeOffset? ResetsAt);
internal sealed record PlanUsageDto(string? Plan, PlanWindowDto[] Windows);
internal sealed record ProviderDto(string Id, bool Enabled, bool KeySet, string Status, string? Detail, CodexDto? Codex, PlanUsageDto? Plan);
internal sealed record AppDto(Guid Id, string Name, string KeyHint, bool Active, string[] Allowed, long? DailyTokens, long? MonthlyTokens, int RatePerMinute, bool AllowFiles,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, BudgetDto Budget);
internal sealed record AiStateDto(bool Enabled, string? DefaultModel, bool CanStoreKeys, ProviderDto[] Providers, AppDto[] Apps);
internal sealed record AppCreatedDto(AppDto App, string Key);
internal sealed record KeyDto(string Key);
internal sealed record AdminModelsDto(ModelDto[] Models);
internal sealed record TestDto(bool Ok, int Models, string? Code);
internal sealed record UsageRowDto(string Day, Guid AppId, string App, string Provider, string Model, int Requests, int Ok, int Failed, int Aborted, long TokensIn, long TokensOut, long Millis);
internal sealed record AdminUsageDto(UsageRowDto[] Rows);
internal sealed record EnabledRequest(bool? Enabled);
internal sealed record DefaultModelRequest(string? Model);
internal sealed record ProviderRequest(bool? Enabled, string? Key);
internal sealed record AppRequest(string? Name, string[]? Allowed, long? DailyTokens, long? MonthlyTokens, int? RatePerMinute, bool? AllowFiles);
internal sealed record ActiveRequest(bool? Active);

/// <summary>JSON ohne Reflection fuer den KI-Zugang (eigener Kontext, damit AppJson unberuehrt bleibt).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AiErrorBody))]
[JsonSerializable(typeof(ChatRequestDto))]
[JsonSerializable(typeof(ChatResponseDto))]
[JsonSerializable(typeof(StartEventDto))]
[JsonSerializable(typeof(TextEventDto))]
[JsonSerializable(typeof(DoneEventDto))]
[JsonSerializable(typeof(ModelsResponseDto))]
[JsonSerializable(typeof(UsageResponseDto))]
[JsonSerializable(typeof(AiStateDto))]
[JsonSerializable(typeof(AppCreatedDto))]
[JsonSerializable(typeof(AppDto))]
[JsonSerializable(typeof(KeyDto))]
[JsonSerializable(typeof(AdminModelsDto))]
[JsonSerializable(typeof(TestDto))]
[JsonSerializable(typeof(AdminUsageDto))]
[JsonSerializable(typeof(LoginDto))]
[JsonSerializable(typeof(EnabledRequest))]
[JsonSerializable(typeof(DefaultModelRequest))]
[JsonSerializable(typeof(ProviderRequest))]
[JsonSerializable(typeof(AppRequest))]
[JsonSerializable(typeof(ActiveRequest))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class AiJson : JsonSerializerContext;
