using System.Text.Json.Serialization;
using JulOS.Backend.Services;
using JulOS.Frontend.About;
using JulOS.Frontend.Agent;
using JulOS.Frontend.Auth;
using JulOS.Backend.Modules;
using JulOS.Frontend.Files;
using JulOS.Frontend.Folders;
using JulOS.Frontend.Hosts;
using JulOS.Frontend.Modules;
using JulOS.Frontend.Settings;
using JulOS.Frontend.Tools;
using JulOS.Integrations.Reachability;
using JulOS.Frontend.SystemInfo;

namespace JulOS.Frontend;

/// <summary>JSON ohne Reflection: alle Typen, die die API sendet oder empfaengt, stehen hier.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(AboutResponse))]
[JsonSerializable(typeof(BackupStatus))]
[JsonSerializable(typeof(CopyStatus))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(SetupBeginRequest))]
[JsonSerializable(typeof(SetupBeginResponse))]
[JsonSerializable(typeof(SetupFinishRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(TotpBeginRequest))]
[JsonSerializable(typeof(TotpBeginResponse))]
[JsonSerializable(typeof(TotpFinishRequest))]
[JsonSerializable(typeof(TotpFinishResponse))]
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
[JsonSerializable(typeof(AgentEnrollRequest))]
[JsonSerializable(typeof(AgentEnrollResponse))]
[JsonSerializable(typeof(AgentCodeResponse))]
[JsonSerializable(typeof(AgentUpdateResponse))]
[JsonSerializable(typeof(AgentAutoUpdateRequest))]
[JsonSerializable(typeof(AgentAutoUpdateResponse))]
[JsonSerializable(typeof(HostDto))]
[JsonSerializable(typeof(HostsResponse))]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(OverviewResponse))]
[JsonSerializable(typeof(FileListingDto))]
[JsonSerializable(typeof(FileSearchDto))]
[JsonSerializable(typeof(ToolTargetsResponse))]
[JsonSerializable(typeof(PingRequest))]
[JsonSerializable(typeof(DnsRequest))]
[JsonSerializable(typeof(PortsRequest))]
[JsonSerializable(typeof(PingOutcome))]
[JsonSerializable(typeof(DnsOutcome))]
[JsonSerializable(typeof(PortsResponse))]
[JsonSerializable(typeof(ModulesResponse))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(OrganisationResponse))]
[JsonSerializable(typeof(FolderRequest))]
[JsonSerializable(typeof(FolderOrderRequest))]
[JsonSerializable(typeof(FolderAssignRequest))]
[JsonSerializable(typeof(FavoriteRequest))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(LocalImportRequest))]
[JsonSerializable(typeof(SettingsResponse))]
[JsonSerializable(typeof(SettingRequest))]
[JsonSerializable(typeof(CaddyListing))]
[JsonSerializable(typeof(CaddyReachability))]
[JsonSerializable(typeof(DdnsStatus))]
[JsonSerializable(typeof(ServiceRecord))]
[JsonSerializable(typeof(ServiceMetric))]
[JsonSerializable(typeof(ServiceStatus))]
[JsonSerializable(typeof(ServicesResponse))]
[JsonSerializable(typeof(ServiceStatusesResponse))]
[JsonSerializable(typeof(ServiceRequest))]
internal sealed partial class AppJson : JsonSerializerContext;
