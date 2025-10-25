using GoombaCast.Audio.AudioHandlers;
using GoombaCast.Audio.Streaming;
using System;
using System.Threading;

namespace GoombaCast.Services
{
    public sealed class AudioEngine : IDisposable
    {
        private readonly IcecastStream _icecastStream;
        private readonly MicrophoneStream _micStream;
        private readonly LevelMeterAudioHandler _levelMeter;
        private readonly GainAudioHandler _gain;

        // Re-expose levels for view models (already marshalled to UI thread)
        public event Action<float, float>? LevelsAvailable;

        public AudioEngine(SynchronizationContext? uiContext)
        {
            _icecastStream = new IcecastStream(new IcecastStreamConfig
            {
                Host = "your.icecast.server",
                Port = 8000,
                Mount = "/stream.mp3",
                User = "source",
                Pass = "yourpassword",
                UseTls = false,
                ContentType = "audio/mpeg",
                StreamName = "My GoombaCast Stream",
                StreamUrl = "http://example.com",
                StreamGenre = "Various"
            });

            _levelMeter = new LevelMeterAudioHandler
            {
                // Ensure callbacks fire on the UI thread
                CallbackContext = uiContext,
                UseRmsLevels = true,
                LevelFloorDb = -90f
            };
            _levelMeter.LevelsAvailable += (l, r) => LevelsAvailable?.Invoke(l, r);

            _gain = new GainAudioHandler();
            _gain.GainDb = 0.0f; // Example: +3 dB gain

            _micStream = new MicrophoneStream(_icecastStream);
            _micStream.AddAudioHandler(_gain);
            _micStream.AddAudioHandler(_levelMeter);
        }

        public void Start()
        {
            // If your IcecastStream requires explicit open, you can call:
            // _micStream.StartBroadcast();
            _micStream.Start();
        }

        public void Stop() => _micStream.Stop();

        public void Dispose() => _micStream.Dispose();
    }
}