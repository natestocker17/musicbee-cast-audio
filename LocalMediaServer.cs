using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    // A small, token-protected HTTP server. Cast receivers fetch media themselves.
    internal sealed class LocalMediaServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object gate = new object();
        private string filePath;
        private string token;

        internal LocalMediaServer()
        {
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            Task.Run(() => AcceptLoop());
        }

        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        internal string Publish(string path, IPAddress localAddress)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The current track is unavailable.", path);
            var bytes = new byte[24];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes);
            lock (gate)
            {
                filePath = path;
                token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                return "http://" + localAddress + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/media/" + token;
            }
        }

        private async Task AcceptLoop()
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (cancellation.IsCancellationRequested) break; else continue; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;
                try
                {
                    var stream = client.GetStream();
                    var header = ReadHeaders(stream);
                    if (header == null) return;
                    var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length < 2 || (first[0] != "GET" && first[0] != "HEAD")) { Respond(stream, 405, "Method Not Allowed"); return; }
                    string path, key;
                    lock (gate) { path = filePath; key = token; }
                    if (first[1] != "/media/" + key || path == null)
                    { Respond(stream, 404, "Not Found"); return; }
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long start = 0, end = file.Length - 1;
                        bool partial = false;
                        foreach (var line in lines)
                        {
                            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                            var value = line.Substring(6).Trim();
                            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!TryParseRange(value.Substring(6), file.Length, out start, out end))
                            { RespondRangeNotSatisfiable(stream, file.Length); return; }
                            partial = true;
                            break;
                        }
                        var length = end - start + 1;
                        var mime = AudioFormat.ContentType(path);
                        var response = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\n" +
                            "Content-Type: " + mime + "\r\nAccept-Ranges: bytes\r\n" +
                            "Content-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                            (partial ? "Content-Range: bytes " + start + "-" + end + "/" + file.Length + "\r\n" : "") +
                            "Connection: close\r\n\r\n";
                        var bytes = Encoding.ASCII.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                        if (first[0] == "HEAD") return;
                        file.Position = start;
                        var buffer = new byte[65536];
                        while (length > 0)
                        {
                            var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                            if (count == 0) break;
                            stream.Write(buffer, 0, count);
                            length -= count;
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static bool TryParseRange(string value, long fileLength, out long start, out long end)
        {
            start = 0;
            end = fileLength - 1;
            if (fileLength == 0 || value.IndexOf(',') >= 0) return false;
            var dash = value.IndexOf('-');
            if (dash < 0 || dash != value.LastIndexOf('-')) return false;
            var first = value.Substring(0, dash);
            var last = value.Substring(dash + 1);
            if (first.Length == 0)
            {
                long suffixLength;
                if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out suffixLength) || suffixLength == 0)
                    return false;
                start = Math.Max(0, fileLength - suffixLength);
            }
            else
            {
                if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= fileLength)
                    return false;
                if (last.Length > 0)
                {
                    if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
                        return false;
                    end = Math.Min(end, fileLength - 1);
                }
            }
            return true;
        }

        private static void RespondRangeNotSatisfiable(Stream stream, long fileLength)
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" +
                fileLength.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string ReadHeaders(Stream stream)
        {
            using (var data = new MemoryStream())
            {
                while (data.Length < 16384)
                {
                    int value = stream.ReadByte();
                    if (value < 0) return null;
                    data.WriteByte((byte)value);
                    var b = data.GetBuffer();
                    var n = (int)data.Length;
                    if (n >= 4 && b[n - 4] == 13 && b[n - 3] == 10 && b[n - 2] == 13 && b[n - 1] == 10)
                        return Encoding.ASCII.GetString(b, 0, n);
                }
            }
            return null;
        }

        private static void Respond(Stream stream, int status, string message)
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + message + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            cancellation.Dispose();
        }
    }

    internal static class AudioFormat
    {
        internal static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".m4a": case ".mp4": return "audio/mp4";
                case ".flac": return "audio/flac";
                case ".wav": return "audio/wav";
                case ".ogg": case ".oga": return "audio/ogg";
                case ".opus": return "audio/ogg; codecs=opus";
                case ".webm": return "audio/webm";
                default: throw new NotSupportedException("Unsupported Cast audio format: " + Path.GetExtension(path));
            }
        }
    }
}
