using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaSecret
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        public static BrQaSecret Read(string path)
        {
            try
            {
                var secret = JsonSerializer.Deserialize(File.ReadAllText(path), BrQaSecretJsonContext.Default.BrQaSecret);
                if (secret == null || secret.SchemaVersion != 1 || string.IsNullOrWhiteSpace(secret.Username) ||
                    string.IsNullOrEmpty(secret.Password) || secret.Username.Length > 64 || secret.Password.Length > 256)
                {
                    throw new InvalidDataException("invalid-secret-contract");
                }

                return secret;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException("Beyond Recall QA secret could not be read or validated.");
            }
        }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BrQaSecret))]
    internal partial class BrQaSecretJsonContext : JsonSerializerContext
    {
    }
}
