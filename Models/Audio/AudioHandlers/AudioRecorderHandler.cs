using GoombaCast.Services;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.IO;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    public class AudioRecorderHandler : IAudioHandler, IDisposable
    {
        private WaveFormat? _fmt;
        private LameMP3FileWriter? _writer;
        private string? _currentFile;
        private bool _disposed;
        private bool _enabled = true;

        public string FriendlyName => "Audio Recorder";
        public int Order => 200; // Run after processing handlers
        public bool Enabled => _enabled;
        public bool IsRecording => _writer != null;

        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!IsRecording || _writer == null) return;
            _writer.Write(buffer, offset, count);
        }

        public void StartRecording(string outputDirectory)
        {
            if (IsRecording) return;

            Directory.CreateDirectory(outputDirectory);

            _currentFile = Path.Combine(outputDirectory,
                $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");

            if (_fmt != null)
            {
                _writer = new LameMP3FileWriter(_currentFile, _fmt, 128);
                _enabled = true;
                Logging.Log($"Started recording to {_currentFile}");
            }
        }

        public void StopRecording()
        {
            _enabled = false;
            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
                Logging.Log($"Stopped recording");
                _currentFile = null;
            }
        }

        public void OnStart(WaveFormat waveFormat)
        {
            _fmt = waveFormat;
            
            // If we were waiting to start recording, do it now
            if (_currentFile != null)
            {
                _writer = new LameMP3FileWriter(_currentFile, _fmt, 128);
                _enabled = true;
            }
        }

        public void OnStop()
        {
            _writer?.Dispose();
            _writer = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            StopRecording();
            _disposed = true;
        }
    }
}