using GoombaCast.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GoombaCast.Models.Audio.Streaming
{
    /// <summary>
    /// Represents statistics from an Icecast streaming server.
    /// </summary>
    public class IcecastStats
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static HttpClient? _httpClient;
        private static HttpClient GetClient() => _httpClient ??= new HttpClient();

        [JsonPropertyName("icestats")]
        public Icestats? Stats { get; set; }

        private static IcecastStats? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<IcecastStats>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                Logging.LogError($"Failed to parse Icecast stats: {ex.Message}");
                return null;
            }
        }

        public static async Task<IcecastStats?> GetStatsAsync()
        {
            var settings = SettingsService.Default.Settings;
            if (string.IsNullOrEmpty(settings.ServerAddress))
            {
                Logging.LogError("Server address is not configured");
                return null;
            }

            try
            {
                Uri uri = new(settings.ServerAddress);
                UriBuilder builder = new("http", uri.Host, 8000, "/status-json.xsl");

                using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                var response = await GetClient().SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return FromJson(json);
            }
            catch (HttpRequestException ex)
            {
                Logging.LogError($"Failed to fetch Icecast stats: {ex.Message}");
                return null;
            }
            catch (UriFormatException ex)
            {
                Logging.LogError($"Invalid server address: {ex.Message}");
                return null;
            }
        }

        public int GetListenerCount()
        {
            if (Stats?.source?.Count <= 0)
                return 0;

            return Stats!.source!.Sum(s => s.listeners);
        }

        public class Icestats
        {
            public string? admin { get; set; }
            public int banned_IPs { get; set; }
            public long build { get; set; }
            public string? host { get; set; }
            public string? location { get; set; }
            public int outgoing_kbitrate { get; set; }
            public string? server_id { get; set; }
            public string? server_start { get; set; }
            public int stream_kbytes_read { get; set; }
            public int stream_kbytes_sent { get; set; }
            [JsonConverter(typeof(SourceConverter))]
            public List<Source>? source { get; set; }
        }

        public class Source
        {
            public string? artist { get; set; }
            public string? audio_info { get; set; }
            public int bitrate { get; set; }
            public int connected { get; set; }
            public string? genre { get; set; }
            public int incoming_bitrate { get; set; }
            public int listener_peak { get; set; }
            public int listeners { get; set; }
            public string? listenurl { get; set; }
            public string? metadata_updated { get; set; }
            public int outgoing_kbitrate { get; set; }
            public int queue_size { get; set; }
            public string? server_description { get; set; }
            public string? server_name { get; set; }
            public string? server_type { get; set; }
            public string? server_url { get; set; }
            public string? stream_start { get; set; }
            public string? title { get; set; }
            public int total_mbytes_sent { get; set; }
            public string? yp_currently_playing { get; set; }
        }

        public class SourceConverter : JsonConverter<List<Source>>
        {
            public override List<Source>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null)
                    return null;

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var source = JsonSerializer.Deserialize<Source>(ref reader, options);
                    return source != null ? new List<Source> { source } : null;
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    // Multiple sources - use source generator context
                    return JsonSerializer.Deserialize<List<Source>>(ref reader, options);
                }

                throw new JsonException($"Unexpected token {reader.TokenType} when parsing source");
            }

            public override void Write(Utf8JsonWriter writer, List<Source> value, JsonSerializerOptions options)
            {
                JsonSerializer.Serialize(writer, value, options);
            }
        }
    }
}
