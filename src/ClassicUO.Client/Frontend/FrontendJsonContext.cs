using System.Text.Json.Serialization;

namespace ClassicUO.Frontend;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FrontendInputMessage))]
[JsonSerializable(typeof(FrontendInstrumentationReport))]
internal partial class FrontendJsonContext : JsonSerializerContext
{
}
