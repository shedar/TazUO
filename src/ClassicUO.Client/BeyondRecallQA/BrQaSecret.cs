using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaSecret
    {
        private const int LoginFieldLength = 30;

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
                    string.IsNullOrEmpty(secret.Password) || secret.Username.Length > LoginFieldLength ||
                    secret.Password.Length > LoginFieldLength || !IsPrintableAscii(secret.Username) ||
                    !IsPrintableAscii(secret.Password))
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

        private static bool IsPrintableAscii(string value)
        {
            foreach (char character in value)
            {
                if (character < '!' || character > '~')
                    return false;
            }

            return true;
        }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BrQaSecret))]
    internal partial class BrQaSecretJsonContext : JsonSerializerContext
    {
    }
}
