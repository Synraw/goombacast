using GoombaCast.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GoombaCast.Models.Audio.Streaming
{
    public class IcecastStats
    {
        private static IcecastStats? FromJson(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var statsWrapper = JsonSerializer.Deserialize<IcecastStats>(json, options);
            return statsWrapper;
        }

        public static async Task<IcecastStats?> GetStatsAsync()
        {
            var s = SettingsService.Default.Settings;
            Uri uri = new(s.ServerAddress??"localhost");
            UriBuilder builder = new("http", uri.Host, 8000, "/status-json.xsl");
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(builder.Uri);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var stats = IcecastStats.FromJson(json);
            return stats;
        }

        public int GetListenerCount()
        {
            if (icestats.source != null && icestats.source.Count > 0)
            {
                return icestats.source.Sum(s => s.listeners);
            }
            return 0;
        }

        public Icestats icestats { get; set; }

        public class Icestats
        {
            public string admin { get; set; }
            public int banned_IPs { get; set; }
            public long build { get; set; }
            public string host { get; set; }
            public string location { get; set; }
            public int outgoing_kbitrate { get; set; }
            public string server_id { get; set; }
            public string server_start { get; set; }
            public int stream_kbytes_read { get; set; }
            public int stream_kbytes_sent { get; set; }
            public List<Source> source { get; set; }
        }

        public class Source
        {
            public string artist { get; set; }
            public string audio_info { get; set; }
            public int bitrate { get; set; }
            public int connected { get; set; }
            public string genre { get; set; }
            public int incoming_bitrate { get; set; }
            public int listener_peak { get; set; }
            public int listeners { get; set; }
            public string listenurl { get; set; }
            public string metadata_updated { get; set; }
            public int outgoing_kbitrate { get; set; }
            public int queue_size { get; set; }
            public string server_description { get; set; }
            public string server_name { get; set; }
            public string server_type { get; set; }
            public string server_url { get; set; }
            public string stream_start { get; set; }
            public string title { get; set; }
            public int total_mbytes_sent { get; set; }
            public string yp_currently_playing { get; set; }
            public object dummy { get; set; }
        }
    }
}
