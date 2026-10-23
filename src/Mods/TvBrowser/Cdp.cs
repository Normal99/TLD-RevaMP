using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TvBrowser
{
    /// The connection to the browser: the Chrome DevTools Protocol over one WebSocket on 127.0.0.1 (RFC 6455, text
    /// frames, no extensions; a few hundred lines, so nothing of Mono's WebSocket stack is needed under Proton).
    /// Commands carry ids; replies and events are read on a worker thread and handed to the main thread (Pump).
    /// A screencast frame's picture is decoded from base64 on the worker, so the main thread only uploads it.
    internal sealed class Cdp : IDisposable
    {
        internal sealed class Message
        {
            public int Id;                            // a reply's command id (0: an event)
            public string Method, Session;            // an event
            public Dictionary<string, object> Params, Result, Error;
            public byte[] Picture;                    // Page.screencastFrame: the JPEG
        }

        private readonly TcpClient _tcp;
        private readonly Stream _s;
        private readonly Thread _reader;
        private readonly object _sendLock = new object();
        private readonly ConcurrentQueue<Message> _in = new ConcurrentQueue<Message>();
        private readonly Dictionary<int, Action<Message>> _pending = new Dictionary<int, Action<Message>>();
        private int _nextId;
        private volatile bool _closed;
        public string Problem;
        public bool Open => !_closed;
        /// Counters for the status line: messages in and out, picture bytes in.
        public long Sent, Received, PictureBytes;

        private Cdp(TcpClient tcp, string path)
        {
            _tcp = tcp; _tcp.NoDelay = true;
            _s = tcp.GetStream();
            Handshake(path);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "TvBrowser CDP" };
            _reader.Start();
        }

        /// Connects to ws://127.0.0.1:port/path (blocking: call it off the main thread).
        public static Cdp Connect(int port, string path)
        {
            var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            return new Cdp(tcp, path);
        }

        private void Handshake(string path)
        {
            var keyBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);
            string req = "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                         "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
            var b = Encoding.ASCII.GetBytes(req);
            _s.Write(b, 0, b.Length);
            // the reply's head, up to the blank line
            var head = new StringBuilder();
            int prev3 = 0, prev2 = 0, prev1 = 0;
            while (head.Length < 8192)
            {
                int c = _s.ReadByte();
                if (c < 0) throw new IOException("the browser closed the connection");
                head.Append((char)c);
                if (prev3 == '\r' && prev2 == '\n' && prev1 == '\r' && c == '\n') break;
                prev3 = prev2; prev2 = prev1; prev1 = c;
            }
            string h = head.ToString();
            if (!h.StartsWith("HTTP/1.1 101")) throw new IOException("the browser refused the connection: " + h.Split('\r')[0]);
            string want;
            using (var sha = SHA1.Create()) want = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            if (h.IndexOf(want, StringComparison.Ordinal) < 0) throw new IOException("the browser's handshake didn't match");
        }

        /// Sends a command; `reply` runs on the main thread (in Pump) with its result or error. Returns the id, or 0 if
        /// the connection is closed.
        public int Send(string method, Dictionary<string, object> args = null, string session = null, Action<Message> reply = null)
        {
            if (_closed) return 0;
            int id = Interlocked.Increment(ref _nextId);
            var msg = new Dictionary<string, object> { ["id"] = id, ["method"] = method };
            if (args != null) msg["params"] = args;
            if (session != null) msg["sessionId"] = session;
            if (reply != null) lock (_pending) _pending[id] = reply;
            try { Frame(1, Encoding.UTF8.GetBytes(Json.Write(msg))); Sent++; }
            catch (Exception e) { Close("send failed: " + e.Message); return 0; }
            return id;
        }

        // a client's frames are masked (RFC 6455 5.3)
        private void Frame(int opcode, byte[] payload)
        {
            lock (_sendLock)
            {
                var head = new List<byte>(14) { (byte)(0x80 | opcode) };
                int n = payload.Length;
                if (n < 126) head.Add((byte)(0x80 | n));
                else if (n < 65536) { head.Add(0x80 | 126); head.Add((byte)(n >> 8)); head.Add((byte)n); }
                else { head.Add(0x80 | 127); for (int i = 7; i >= 0; i--) head.Add((byte)((long)n >> (8 * i))); }
                var mask = new byte[4];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);
                head.AddRange(mask);
                var masked = new byte[n];
                for (int i = 0; i < n; i++) masked[i] = (byte)(payload[i] ^ mask[i & 3]);
                var h = head.ToArray();
                _s.Write(h, 0, h.Length);
                _s.Write(masked, 0, n);
                _s.Flush();
            }
        }

        /// Hands what arrived to the main thread: replies to their callbacks, events to `onEvent`.
        public void Pump(Action<Message> onEvent, int max = 200)
        {
            for (int n = 0; n < max && _in.TryDequeue(out var m); n++)
            {
                if (m.Id != 0)
                {
                    Action<Message> cb = null;
                    lock (_pending) if (_pending.TryGetValue(m.Id, out cb)) _pending.Remove(m.Id);
                    try { cb?.Invoke(m); } catch (Exception e) { Plugin.Log.LogError("browser reply: " + e); }
                }
                else
                {
                    try { onEvent(m); } catch (Exception e) { Plugin.Log.LogError("browser event " + m.Method + ": " + e); }
                }
            }
        }

        public const int MaxMessage = 32 * 1024 * 1024;

        private void ReadLoop()
        {
            var buf = new MemoryStream();
            try
            {
                while (!_closed)
                {
                    int b0 = _s.ReadByte(), b1 = _s.ReadByte();
                    if (b0 < 0 || b1 < 0) { Close("the browser closed the connection"); return; }
                    bool fin = (b0 & 0x80) != 0; int op = b0 & 0x0f;
                    long len = b1 & 0x7f;
                    if ((b1 & 0x7f) == 126) { var l = ReadExactInto(2); len = (l[0] << 8) | l[1]; }
                    else if ((b1 & 0x7f) == 127) { var l = ReadExactInto(8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | l[i]; }
                    if (len < 0 || len > MaxMessage) { Close("a message too large from the browser"); return; }
                    byte[] mask = (b1 & 0x80) != 0 ? ReadExactInto(4) : null;
                    var payload = ReadExactInto((int)len);
                    if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                    switch (op)
                    {
                        case 0: case 1: case 2:
                            buf.Write(payload, 0, payload.Length);
                            if (buf.Length > MaxMessage) { Close("a message too large from the browser"); return; }
                            if (!fin) break;
                            var text = Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
                            buf.SetLength(0);
                            Received++;
                            Take(text);
                            break;
                        case 8: Close("the browser closed the connection"); return;
                        case 9: try { Frame(10, payload); } catch { } break;   // ping: pong
                    }
                }
            }
            catch (Exception e) { Close(_closed ? Problem : "connection lost: " + e.Message); }
        }

        private byte[] ReadExactInto(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, got, n - got);
                if (r <= 0) throw new IOException("the browser closed the connection");
                got += r;
            }
            return b;
        }

        private void Take(string text)
        {
            if (!(Json.Parse(text) is Dictionary<string, object> o)) return;
            var m = new Message { Id = Json.Int(o, "id"), Method = Json.Str(o, "method"), Session = Json.Str(o, "sessionId"),
                                  Params = Json.Obj(o, "params"), Result = Json.Obj(o, "result"), Error = Json.Obj(o, "error") };
            if (m.Method == "Page.screencastFrame" && m.Params != null)
            {
                string data = Json.Str(m.Params, "data");
                try { m.Picture = data != null ? Convert.FromBase64String(data) : null; } catch (FormatException) { m.Picture = null; }
                m.Params.Remove("data");
                if (m.Picture != null) PictureBytes += m.Picture.Length;
            }
            _in.Enqueue(m);
        }

        public void Close(string why)
        {
            if (_closed) return;
            _closed = true; Problem = why;
            try { _tcp.Close(); } catch { }
        }

        public void Dispose() => Close("closed");
    }
}
