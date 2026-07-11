using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// Minimal hand-rolled WebSocket client (RFC 6455) over TcpClient + SslStream.
    ///
    /// WHY THIS EXISTS:
    /// Unity ships an ancient Mono runtime whose System.Net.WebSockets.ClientWebSocket
    /// routinely fails wss handshakes with the opaque "Unable to connect to the remote
    /// server" (verified: the EXACT same URL/token/yy that the desktop .NET
    /// ClientWebSocket connects to in ~14 ms in PowerShell fails inside RimWorld).
    /// Mono's WebSocket transport also ignores ServicePointManager.SecurityProtocol.
    ///
    /// By going down to TcpClient + SslStream (both fully supported by Unity Mono),
    /// we bypass Mono's broken WebSocket layer entirely. We only implement the subset
    /// of RFC 6455 actually needed: client handshake, send text frame, receive frames
    /// (text/fragmented/binary/close/pong), graceful close.
    /// </summary>
    internal sealed class MiniMaxRawWebSocket : IDisposable
    {
        private const string WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly string _host;
        private readonly int _port;
        private readonly string _path;     // path + ?query
        private readonly string _origin;
        private readonly string _cookie;   // full Cookie header value, may be ""

        private TcpClient _tcp;
        private SslStream _ssl;
        private Stream _stream;            // _ssl once connected
        private bool _disposed;

        public bool IsConnected => _stream != null && _tcp != null && _tcp.Connected;

        public MiniMaxRawWebSocket(string host, int port, string pathWithQuery, string origin, string cookie)
        {
            _host = host;
            _port = port;
            _path = pathWithQuery;
            _origin = origin ?? "";
            _cookie = cookie ?? "";
        }

        /// <summary>Open TCP, TLS-upgrade, then send the WS Upgrade request and verify 101.</summary>
        public async Task ConnectAsync(CancellationToken ct)
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_host, _port);

            _ssl = new SslStream(_tcp.GetStream(), false, ValidateServerCert, null);
            // Disable certificate revocation (CRL/OCSP) checks explicitly — Mono's SslStream
            // is known to stall for MINUTES doing unreachable-OCSP/CRL retries. The remote-cert
            // validation callback above already does the chain check we care about.
            try { System.Net.ServicePointManager.CheckCertificateRevocationList = false; } catch { }
            await _ssl.AuthenticateAsClientAsync(_host);
            _stream = _ssl;

            // Build the client handshake. Sec-WebSocket-Key must be a base64'd 16-byte
            // random blob; the server echoes it back as Sec-WebSocket-Accept.
            byte[] keyBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keyBytes);
            string wsKey = Convert.ToBase64String(keyBytes);

            // Header order roughly matches Chrome's actual on-the-wire order.
            // Several headers (Sec-WebSocket-Extensions, Accept-Encoding) are MANDATORY:
            // Alibaba's WAF (alb_request_id) fingerprints on them and 403s on absence.
            var req = new StringBuilder();
            req.Append("GET ").Append(_path).Append(" HTTP/1.1\r\n");
            req.Append("Host: ").Append(_host).Append("\r\n");
            req.Append("Connection: Upgrade\r\n");
            req.Append("Pragma: no-cache\r\n");
            req.Append("Cache-Control: no-cache\r\n");
            req.Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36\r\n");
            req.Append("Upgrade: websocket\r\n");
            req.Append("Origin: ").Append(_origin).Append("\r\n");
            req.Append("Sec-WebSocket-Version: 13\r\n");
            req.Append("Sec-WebSocket-Key: ").Append(wsKey).Append("\r\n");
            req.Append("Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits\r\n");
            req.Append("Accept-Encoding: gzip, deflate, br, zstd\r\n");
            req.Append("Accept-Language: zh-CN,zh;q=0.9,en;q=0.8,en-US;q=0.7\r\n");
            if (!string.IsNullOrEmpty(_cookie)) req.Append("Cookie: ").Append(_cookie).Append("\r\n");
            req.Append("\r\n");

            byte[] reqBytes = Encoding.UTF8.GetBytes(req.ToString());
            await _stream.WriteAsync(reqBytes, 0, reqBytes.Length, ct);
            await _stream.FlushAsync(ct);

            // Read the HTTP/1.1 101 Switching Protocols response headers (up to \r\n\r\n).
            string response = await ReadHttpResponseHeadersAsync(ct);

            // Validate 101 + Accept.
            if (!response.StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WebSocket handshake rejected. Server response (first line + headers):\n" + response);
            }

            // Verify Sec-WebSocket-Accept to be spec-correct.
            string expectedAccept = ComputeAccept(wsKey);
            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();
                if (name.Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                {
                    if (val != expectedAccept)
                        throw new InvalidOperationException("WebSocket Sec-WebSocket-Accept mismatch (expected=" + expectedAccept + ", got=" + val + ").");
                    break;
                }
            }
            // From here on, the same TCP/TLS stream is a raw WebSocket frame channel.
        }

        /// <summary>Send a text message (opcode 0x1, fin=1, masked per RFC 6455 client rule).</summary>
        public async Task SendTextAsync(string text, CancellationToken ct)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            byte[] frame = EncodeClientFrame(0x1, payload);
            await _stream.WriteAsync(frame, 0, frame.Length, ct);
            await _stream.FlushAsync(ct);
        }

        /// <summary>
        /// Receive one logical message (auto-stitches fragmented frames). Returns null
        /// when the server sent a Close frame OR abruptly terminated the TCP stream
        /// (the latter is common: some gateways RST the socket instead of sending an
        /// RFC-6455 Close frame, especially under concurrency / risk-control scrubs).
        /// Returning null lets the caller treat the break as a normal session end and
        /// still use any audio URLs collected up to that point.
        /// </summary>
        public async Task<string> ReceiveMessageAsync(CancellationToken ct)
        {
            var payloadAccum = new List<byte>();
            int opcode = 0;
            while (true)
            {
                FrameInfo frame;
                try
                {
                    frame = await ReadFrameAsync(ct);
                }
                catch (IOException)
                {
                    // TCP RST / FIN / "host software aborted an established
                    // connection" mid-frame = server hung up. Treat as Close.
                    // (EndOfStreamException is a subtype of IOException and is also
                    // caught here, so no separate clause is needed.)
                    return null;
                }
                if (frame.Opcode == 0x8) // Close
                {
                    return null;
                }
                if (frame.Opcode == 0x9) // Ping → reply Pong with same payload
                {
                    byte[] pong = EncodeClientFrame(0xA, frame.Payload);
                    try { await _stream.WriteAsync(pong, 0, pong.Length, ct); await _stream.FlushAsync(ct); } catch { }
                    continue;
                }
                if (frame.Opcode == 0xA) // Pong → ignore
                {
                    continue;
                }
                // 0x1 = text, 0x2 = binary, 0x0 = continuation
                if (frame.Opcode == 0x1 || frame.Opcode == 0x2) opcode = frame.Opcode;
                payloadAccum.AddRange(frame.Payload);
                if (frame.Fin) break;
            }
            // MiniMax sends JSON text; binary path is unused but keep correct.
            return opcode == 0x2
                ? Convert.ToBase64String(payloadAccum.ToArray())
                : Encoding.UTF8.GetString(payloadAccum.ToArray());
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            if (_stream == null) return;
            try
            {
                byte[] closeFrame = EncodeClientFrame(0x8, new byte[0]);
                await _stream.WriteAsync(closeFrame, 0, closeFrame.Length, ct);
                await _stream.FlushAsync(ct);
            }
            catch { /* best-effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ssl?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _stream = null;
        }

        // ----------------------------------------------------------------------
        // RFC 6455 framing helpers
        // ----------------------------------------------------------------------
        private struct FrameInfo
        {
            public int Opcode;
            public bool Fin;
            public byte[] Payload;
        }

        private static byte[] EncodeClientFrame(int opcode, byte[] payload)
        {
            // Clients MUST mask. First byte: FIN(1) RSV1-3(000) opcode(4 bits)
            byte b0 = (byte)(0x80 | (opcode & 0x0F));
            // Server frames are NOT masked in our direction; we send masked.
            int len = payload.Length;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(b0);
                byte[] mask = new byte[4];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);
                if (len < 126)
                {
                    ms.WriteByte((byte)(0x80 | len));
                }
                else if (len <= 0xFFFF)
                {
                    ms.WriteByte((byte)(0x80 | 126));
                    ms.WriteByte((byte)((len >> 8) & 0xFF));
                    ms.WriteByte((byte)(len & 0xFF));
                }
                else
                {
                    ms.WriteByte((byte)(0x80 | 127));
                    // 8-byte length, high 4 bytes are 0 here (we won't exceed int32)
                    ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
                    ms.WriteByte((byte)((len >> 24) & 0xFF));
                    ms.WriteByte((byte)((len >> 16) & 0xFF));
                    ms.WriteByte((byte)((len >> 8) & 0xFF));
                    ms.WriteByte((byte)(len & 0xFF));
                }
                ms.Write(mask, 0, 4);
                for (int i = 0; i < payload.Length; i++)
                {
                    ms.WriteByte((byte)(payload[i] ^ mask[i & 3]));
                }
                return ms.ToArray();
            }
        }

        private async Task<FrameInfo> ReadFrameAsync(CancellationToken ct)
        {
            byte[] hdr = await ReadAsync(2, ct);
            bool fin = (hdr[0] & 0x80) != 0;
            int opcode = hdr[0] & 0x0F;
            bool masked = (hdr[1] & 0x80) != 0;
            long payloadLen = hdr[1] & 0x7F;
            if (payloadLen == 126) { byte[] ext = await ReadAsync(2, ct); payloadLen = (ext[0] << 8) | ext[1]; }
            else if (payloadLen == 127) { byte[] ext = await ReadAsync(8, ct); payloadLen = 0; for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | ext[i]; }

            byte[] mask = null;
            if (masked) mask = await ReadAsync(4, ct);

            // Cap to a sane upper bound to avoid hostile-frame OOM.
            if (payloadLen > 8 * 1024 * 1024) throw new InvalidOperationException("WS frame too large: " + payloadLen);

            byte[] payload = payloadLen > 0 ? await ReadAsync((int)payloadLen, ct) : new byte[0];
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            }
            return new FrameInfo { Opcode = opcode, Fin = fin, Payload = payload };
        }

        private async Task<byte[]> ReadAsync(int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await _stream.ReadAsync(buf, read, count - read, ct);
                if (n == 0) throw new EndOfStreamException("WebSocket stream closed mid-frame.");
                read += n;
            }
            return buf;
        }

        private async Task<string> ReadHttpResponseHeadersAsync(CancellationToken ct)
        {
            // Read byte-by-byte until we see \r\n\r\n. Headers are small for a 101.
            var ms = new MemoryStream();
            byte[] one = new byte[1];
            byte[] tail = new byte[4];
            while (true)
            {
                int n = await _stream.ReadAsync(one, 0, 1, ct);
                if (n == 0) throw new EndOfStreamException("Connection closed during HTTP response read.");
                ms.Write(one, 0, 1);
                tail[0] = tail[1]; tail[1] = tail[2]; tail[2] = tail[3]; tail[3] = one[0];
                if (tail[0] == 0x0D && tail[1] == 0x0A && tail[2] == 0x0D && tail[3] == 0x0A) break;
                if (ms.Length > 16 * 1024) throw new InvalidOperationException("HTTP response headers too large (>16KB).");
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string ComputeAccept(string wsKey)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(wsKey + WS_GUID));
                return Convert.ToBase64String(hash);
            }
        }

        private static bool ValidateServerCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            // MiniMax presents a valid cert from a public CA. Accept only if no errors;
            // accept only the name-mismatch-free path. We deliberately DO NOT accept
            // unknown CAs here — the desktop diagnostic showed TLS validates fine.
            return errors == SslPolicyErrors.None;
        }
    }
}
