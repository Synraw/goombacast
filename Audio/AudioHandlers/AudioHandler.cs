using NAudio.Wave;
using System;

namespace GoombaCast.Audio.AudioHandlers
{
    // Callback-style interface for buffer observation or in-place modification.
    // Called on the audio capture thread; keep work lightweight.
    public interface AudioHandler
    {
        // Processing order; lower runs earlier. Default = 0.
        int Order => 0;

        // Allows handler to be toggled without removal.
        bool Enabled => true;

        // Called once when capture starts and the format is known.
        void OnStart(WaveFormat waveFormat) { }

        // Called for each captured buffer. Modify 'buffer' in-place if desired.
        // 'offset' and 'count' describe the valid region in 'buffer'.
        void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat);

        // Called when capture stops (or handler is removed while running).
        void OnStop() { }
    }
}