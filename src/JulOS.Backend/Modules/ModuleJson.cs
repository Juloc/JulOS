using System.Text.Json.Serialization;

namespace JulOS.Backend.Modules;

/// <summary>JSON ohne Reflection fuer die Typen, die das Backend selbst liest und schreibt (der Server schaltet Reflection ab).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ServiceMetric[]))]
internal sealed partial class ModuleJson : JsonSerializerContext;
