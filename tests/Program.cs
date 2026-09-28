using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MusicBeePlugin;
using Sharpcaster;

class Program
{
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--discover")
        {
            using (var locator = new ChromecastLocator())
            {
                var devices = locator.FindReceiversAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                foreach (var device in devices) Console.WriteLine(device.Name + " " + device.DeviceUri);
                Console.WriteLine("Discovered: " + devices.Count());
            }
            return;
        }
        var path = Path.GetTempFileName() + ".mp3";
        var flacPath = Path.GetTempFileName() + ".flac";
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("0123456789"));
        File.WriteAllBytes(flacPath, Encoding.ASCII.GetBytes("0123456789"));
        try
        {
            var now = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
            var smooth = new SeekDetector();
            smooth.Reset(10000, true, now);
            Check(!smooth.ShouldSeek(11000, true, now.AddSeconds(1)), "normal progression is not a seek");
            Check(!smooth.ShouldSeek(11000, true, now.AddSeconds(4)), "a stalled clock is not a rewind");
            Check(!smooth.ShouldSeek(14000, true, now.AddSeconds(4.5)), "clock catch-up after a stall is not a seek");
            Check(!smooth.ShouldSeek(14500, true, now.AddSeconds(5)), "playback remains smooth after catch-up");
            var gradualCatchUp = new SeekDetector();
            gradualCatchUp.Reset(10000, true, now);
            Check(!gradualCatchUp.ShouldSeek(10000, true, now.AddSeconds(4)), "stall retains its time anchor");
            Check(!gradualCatchUp.ShouldSeek(11000, true, now.AddSeconds(4.5)), "partial catch-up is not a seek");
            Check(!gradualCatchUp.ShouldSeek(14000, true, now.AddSeconds(5)), "later catch-up is not a seek");
            var transient = new SeekDetector();
            transient.Reset(10000, true, now);
            Check(!transient.ShouldSeek(0, true, now.AddSeconds(1)), "one bad position sample is ignored");
            Check(!transient.ShouldSeek(11500, true, now.AddSeconds(1.5)), "a recovered position sample cancels a seek");
            var forward = new SeekDetector();
            forward.Reset(10000, true, now);
            Check(!forward.ShouldSeek(30000, true, now.AddSeconds(1)), "forward seek needs confirmation");
            Check(forward.ShouldSeek(30500, true, now.AddSeconds(1.5)), "confirmed forward seek");
            var backward = new SeekDetector();
            backward.Reset(30000, true, now);
            Check(!backward.ShouldSeek(10000, true, now.AddSeconds(1)), "backward seek needs confirmation");
            Check(backward.ShouldSeek(10500, true, now.AddSeconds(1.5)), "confirmed backward seek");
            var paused = new SeekDetector();
            paused.Reset(10000, false, now);
            Check(!paused.ShouldSeek(20000, false, now.AddSeconds(1)), "paused seek needs confirmation");
            Check(paused.ShouldSeek(20000, false, now.AddSeconds(1.5)), "confirmed paused seek");
            using (var locator = new ChromecastLocator())
            {
                var client = new ChromecastClient();
                Check(client.MediaChannel != null, "Cast SDK runtime");
            }
            using (var server = new LocalMediaServer())
            {
                var url = server.Publish(path, IPAddress.Loopback);
                using (var response = (HttpWebResponse)WebRequest.Create(url).GetResponse())
                using (var stream = new StreamReader(response.GetResponseStream()))
                {
                    Check(response.StatusCode == HttpStatusCode.OK, "GET status");
                    Check(response.ContentType == "audio/mpeg", "MIME");
                    Check(stream.ReadToEnd() == "0123456789", "GET body");
                }
                var rangeRequest = (HttpWebRequest)WebRequest.Create(url);
                rangeRequest.AddRange(3, 6);
                using (var response = (HttpWebResponse)rangeRequest.GetResponse())
                using (var stream = new StreamReader(response.GetResponseStream()))
                {
                    Check(response.StatusCode == HttpStatusCode.PartialContent, "range status");
                    Check(response.Headers["Content-Range"] == "bytes 3-6/10", "range header");
                    Check(stream.ReadToEnd() == "3456", "range body");
                }
                var head = (HttpWebRequest)WebRequest.Create(url);
                head.Method = "HEAD";
                using (var response = (HttpWebResponse)head.GetResponse())
                    Check(response.ContentLength == 10, "HEAD length");
                try { WebRequest.Create(url + "bad").GetResponse(); throw new Exception("token accepted"); }
                catch (WebException ex) { Check(((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.NotFound, "bad token"); }

                var flacUrl = server.Publish(flacPath, IPAddress.Loopback);
                var suffix = (HttpWebRequest)WebRequest.Create(flacUrl);
                suffix.AddRange(-3);
                using (var response = (HttpWebResponse)suffix.GetResponse())
                using (var stream = new StreamReader(response.GetResponseStream()))
                {
                    Check(response.ContentType == "audio/flac", "FLAC MIME");
                    Check(response.Headers["Content-Range"] == "bytes 7-9/10", "suffix range header");
                    Check(stream.ReadToEnd() == "789", "suffix range body");
                }
                var oversized = (HttpWebRequest)WebRequest.Create(flacUrl);
                oversized.AddRange(8, 1000);
                using (var response = (HttpWebResponse)oversized.GetResponse())
                using (var stream = new StreamReader(response.GetResponseStream()))
                {
                    Check(response.Headers["Content-Range"] == "bytes 8-9/10", "oversized range is clamped");
                    Check(stream.ReadToEnd() == "89", "oversized range body");
                }
            }
            Console.WriteLine("PASS: " + checks + " server checks");
        }
        finally { File.Delete(path); File.Delete(flacPath); }
    }
}
