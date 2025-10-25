using System;
using Avalonia;
using GoombaCast.Audio.Streaming;

namespace GoombaCast
{
    internal sealed class Program
    {
        static IcecastStream? icecastStream;
        static MicrophoneStream? microphoneStream;


        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args) {

            icecastStream = new IcecastStream(new IcecastStreamConfig
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

            MicrophoneStream micStream = new MicrophoneStream(icecastStream);

            var levelsHandler = new Audio.AudioHandlers.LevelMeterAudioHandler();

            levelsHandler.LevelsAvailable += (leftDb, rightDb) =>
            {
                // Example: print levels to console
                Console.WriteLine($"Levels - Left: {leftDb:F1} dB, Right: {rightDb:F1} dB");
            };

            micStream.AddAudioHandler(new Audio.AudioHandlers.GainAudioHandler());
            micStream.AddAudioHandler(levelsHandler);

            microphoneStream = micStream;
            microphoneStream.Start();


            BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
