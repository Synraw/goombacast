using GoombaCast.Models.Audio.Streaming;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GoombaCast.Services
{
    public class IceStats
    {
        private HttpClient _httpClient;

        public IceStats()
        {
            _httpClient = new HttpClient();
        }

        
    }
}
