using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast.Audio.Streaming
{
    public class IcecastStreamConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 8000;
        public string Mount { get; set; } = "/";
        public string User { get; set; } = "source";
        public string Pass { get; set; } = "hackme";
        public bool UseTls { get; set; } = false;
        public string ContentType { get; set; } = "audio/mpeg";
        public string? StreamName { get; set; } = "GoomaCast Stream";
        public string? StreamUrl { get; set; }
        public string? StreamGenre { get; set; } = "Radio";
        public bool IsPublic { get; set; } = true;

        public string GetIcecastHeaders()
        {
            var sb = new StringBuilder();
            sb.Append($"SOURCE {Mount} ICE/1.0\r\n");
            sb.Append($"content-type: {ContentType}\r\n");
            sb.Append($"Authorization: Basic {Base64($"{User}:{Pass}")}\r\n");

            // Optional Icecast "ice-*" headers (nice to have; not required)
            if (!string.IsNullOrWhiteSpace(StreamName)) sb.Append($"ice-name: {StreamName}\r\n");
            if (!string.IsNullOrWhiteSpace(StreamUrl)) sb.Append($"ice-url: {StreamUrl}\r\n");
            if (!string.IsNullOrWhiteSpace(StreamGenre)) sb.Append($"ice-genre: {StreamGenre}\r\n");

            sb.Append($"ice-bitrate: 320\r\n");
            sb.Append($"ice-private: {(IsPublic ? "0" : "1")}\r\n");
            sb.Append($"ice-public: {(IsPublic ? "1" : "0")}\r\n");
            sb.Append($"ice-audio-info: ice-samplerate=48000;ice-bitrate=320;ice-channels=2\r\n");
            sb.Append("\r\n");

            return sb.ToString();
        }

        private static string Base64(string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));
    }

    public class IcecastStream : Stream
    {
        private readonly IcecastStreamConfig _icecastConfig;

        private TcpClient? _tcp;
        private Stream? _net;
        private bool _open;

        public IcecastStream(IcecastStreamConfig cfg)
        {
            _icecastConfig = cfg;
        }

        public async Task OpenAsync(CancellationToken ct = default)
        {
            if (_open) return;

            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_icecastConfig.Host, _icecastConfig.Port, ct).ConfigureAwait(false);
            _net = _tcp.GetStream();

            if (_icecastConfig.UseTls)
            {
                var ssl = new SslStream(_net, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _icecastConfig.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct).ConfigureAwait(false);
                _net = ssl;
            }


            // Send headers
            var headerBytes = Encoding.ASCII.GetBytes(_icecastConfig.GetIcecastHeaders());
            await _net.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
            await _net.FlushAsync(ct).ConfigureAwait(false);

            // Read the HTTP response line (should be 100/200 range; Icecast often responds 200 OK)
            using (var reader = new StreamReader(_net, Encoding.ASCII, false, 1024, leaveOpen: true))
            {
                var status = await reader.ReadLineAsync().ConfigureAwait(false);
                if (status == null || (!status.Contains("200") && !status.Contains("100") && !status.Contains("OK")))
                    throw new InvalidOperationException($"Icecast PUT failed: '{status ?? "<no status>"}'");

                // Consume remaining response headers until blank line
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false))) { /* skip */ }
            }

            _open = true;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_open || _net == null) return;

            _net.Write(buffer, offset, count);
            _net.Flush();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_open || _net == null) return;
            await _net.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
        }

        public override void Flush() { /* nothing; we flush per chunk */ }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_open && _net != null)
                    {
                        var end = Encoding.ASCII.GetBytes("0\r\n\r\n");
                        _net.Write(end, 0, end.Length);
                        _net.Flush();
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    _open = false;
                    try { _net?.Dispose(); } catch { }
                    try { _tcp?.Close(); } catch { }
                    _net = null; _tcp = null;
                }
            }

            base.Dispose(disposing);
        }

        public bool IsOpen => _open;

        // Stream contract
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
