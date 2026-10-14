// ------------------------------------------------------------------------------------------------
// Bridge.cs — TCP bridge between the APS solver (this DLL) and Python, version 2.
//
// Design rules:
//   * The solver thread NEVER blocks: it only enqueues outgoing lines and dequeues incoming
//     commands. Socket I/O runs on background threads.
//   * One message per line (newline-delimited JSON, UTF-8). TCP guarantees order and no loss
//     while the client is connected; lines produced with no client connected are counted as dropped.
//   * The listener is static: it lives as long as the APS server process, across runs and
//     snapshot reverts. Python may connect, disconnect and reconnect at any time.
// ------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SimCentral.Examples.tanques4
{
    internal static class Bridge
    {
        public const string Version = "tanques4-bridge 2.0 (TCP, step-synchronous)";
        public const int Port = 5006;
        private const int OutboxCapacity = 500000;

        private static readonly object startLock = new object();
        private static bool started;
        private static TcpListener listener;

        private static readonly BlockingCollection<string> outbox =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), OutboxCapacity);
        private static readonly ConcurrentQueue<Dictionary<string, string>> inbox =
            new ConcurrentQueue<Dictionary<string, string>>();

        private static readonly object clientLock = new object();
        private static TcpClient client;
        private static volatile StreamWriter writer;   // read without lock by the writer thread
        private static volatile bool connected;        // read without lock by the solver thread

        private static long dropped;
        private static long sent;
        private static string lastError = string.Empty;

        /// <summary>Called by the reader thread when Python sends {"cmd":"flush"}.</summary>
        public static Action FlushHandler;

        /// <summary>Called by the reader thread when Python sends {"cmd":"status"}.</summary>
        public static Func<string> StatusHandler;

        public static long Dropped { get { return Interlocked.Read(ref dropped); } }
        public static long Sent { get { return Interlocked.Read(ref sent); } }
        public static bool Connected { get { return connected; } }
        public static string LastError { get { return lastError; } set { lastError = value ?? string.Empty; } }

        public static void EnsureStarted()
        {
            lock (startLock)
            {
                if (started) return;
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, Port);
                    listener.Start();
                    StartThread(AcceptLoop, "bridge-accept");
                    StartThread(WriteLoop, "bridge-writer");
                    started = true;
                }
                catch (Exception ex)
                {
                    lastError = "listener: " + ex.Message;
                }
            }
        }

        /// <summary>Non-blocking: enqueue one JSON line for Python.</summary>
        public static void Send(string jsonLine)
        {
            if (!Connected || !outbox.TryAdd(jsonLine))
            {
                Interlocked.Increment(ref dropped);
            }
        }

        public static bool TryReceive(out Dictionary<string, string> command)
        {
            return inbox.TryDequeue(out command);
        }

        // ---------------------------------------------------------------- threads
        private static void StartThread(ThreadStart body, string name)
        {
            var th = new Thread(body) { IsBackground = true, Name = name };
            th.Start();
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    c.NoDelay = true;
                    var w = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = false };
                    lock (clientLock)
                    {
                        CloseClientUnlocked();
                        client = c;
                        writer = w;
                        connected = true;
                    }
                    var reader = new StreamReader(c.GetStream(), new UTF8Encoding(false));
                    StartThread(() => ReadLoop(c, reader), "bridge-reader");
                    Send(Json.Obj("type", "hello", "version", Version, "port", Port));
                }
                catch (Exception ex)
                {
                    lastError = "accept: " + ex.Message;
                    Thread.Sleep(200);
                }
            }
        }

        private static void ReadLoop(TcpClient c, StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Dictionary<string, string> msg;
                    if (!Json.TryParseFlat(line, out msg))
                    {
                        Send(Json.Obj("type", "error", "msg", "invalid json", "line", line));
                        continue;
                    }
                    string cmd;
                    msg.TryGetValue("cmd", out cmd);
                    cmd = (cmd ?? "set").ToLowerInvariant();

                    if (cmd == "ping")
                    {
                        string id; msg.TryGetValue("id", out id);
                        Send(Json.Obj("type", "pong", "id", id ?? "", "sent", Sent, "dropped", Dropped));
                    }
                    else if (cmd == "flush")
                    {
                        var h = FlushHandler;
                        if (h != null) h();
                        Send(Json.Obj("type", "flushed"));
                    }
                    else if (cmd == "status")
                    {
                        var h = StatusHandler;
                        Send(h != null ? h() : Json.Obj("type", "status", "error", "no equation set"));
                    }
                    else
                    {
                        inbox.Enqueue(msg);   // processed by the solver thread at a safe point
                        string id; msg.TryGetValue("id", out id);
                        // Receipt confirmation: Python waits for it before starting the solver,
                        // so the command is guaranteed to be pending at the first evaluation.
                        Send(Json.Obj("type", "queued", "id", id ?? "", "cmd", cmd));
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = "read: " + ex.Message;
            }
            finally
            {
                lock (clientLock)
                {
                    if (client == c) CloseClientUnlocked();
                }
            }
        }

        private static void WriteLoop()
        {
            // Socket writes happen outside clientLock, so a slow reader on the Python side can
            // only delay this thread; the solver thread never waits on it.
            foreach (string line in outbox.GetConsumingEnumerable())
            {
                StreamWriter w = writer;
                if (w == null)
                {
                    Interlocked.Increment(ref dropped);
                    continue;
                }
                try
                {
                    w.Write(line);
                    w.Write('\n');
                    if (outbox.Count == 0) w.Flush();
                    Interlocked.Increment(ref sent);
                }
                catch (Exception ex)
                {
                    lastError = "write: " + ex.Message;
                    Interlocked.Increment(ref dropped);
                    lock (clientLock)
                    {
                        if (writer == w) CloseClientUnlocked();
                    }
                }
            }
        }

        private static void CloseClientUnlocked()
        {
            try { if (writer != null) writer.Dispose(); } catch { }
            try { if (client != null) client.Close(); } catch { }
            connected = false;
            writer = null;
            client = null;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Minimal JSON helpers for flat objects (no nesting), culture-invariant, round-trip doubles.
    // ------------------------------------------------------------------------------------------------
    internal static class Json
    {
        public static string Num(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return "null";
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Build {"k1":v1,"k2":v2,...}. Strings are quoted, numbers/bools are not.</summary>
        public static string Obj(params object[] kv)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kv[i]).Append("\":");
                object val = kv[i + 1];
                if (val is double) sb.Append(Num((double)val));
                else if (val is float) sb.Append(Num((float)val));
                else if (val is int || val is long) sb.Append(Convert.ToString(val, CultureInfo.InvariantCulture));
                else if (val is bool) sb.Append((bool)val ? "true" : "false");
                else if (val == null) sb.Append("null");
                else sb.Append('"').Append(Escape(val.ToString())).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        }

        /// <summary>Parse a flat JSON object into key -> raw value (strings unquoted).</summary>
        public static bool TryParseFlat(string text, out Dictionary<string, string> result)
        {
            result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (text == null) return false;
            int i = 0, n = text.Length;
            SkipWs(text, ref i);
            if (i >= n || text[i] != '{') return false;
            i++;
            while (true)
            {
                SkipWs(text, ref i);
                if (i < n && text[i] == '}') return true;
                string key;
                if (!ReadString(text, ref i, out key)) return false;
                SkipWs(text, ref i);
                if (i >= n || text[i] != ':') return false;
                i++;
                SkipWs(text, ref i);
                string value;
                if (i < n && text[i] == '"')
                {
                    if (!ReadString(text, ref i, out value)) return false;
                }
                else
                {
                    int start = i;
                    while (i < n && text[i] != ',' && text[i] != '}') i++;
                    value = text.Substring(start, i - start).Trim();
                }
                result[key] = value;
                SkipWs(text, ref i);
                if (i < n && text[i] == ',') { i++; continue; }
                if (i < n && text[i] == '}') return true;
                return false;
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static bool ReadString(string s, ref int i, out string value)
        {
            value = null;
            if (i >= s.Length || s[i] != '"') return false;
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char ch = s[i++];
                if (ch == '\\' && i < s.Length) { sb.Append(s[i++]); continue; }
                if (ch == '"') { value = sb.ToString(); return true; }
                sb.Append(ch);
            }
            return false;
        }

        public static bool TryDouble(Dictionary<string, string> d, string key, out double x)
        {
            x = double.NaN;
            string raw;
            return d.TryGetValue(key, out raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && !double.IsNaN(x) && !double.IsInfinity(x);
        }
    }
}
