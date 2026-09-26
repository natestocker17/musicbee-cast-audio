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
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("0123456789"));
        try
        {
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
            }
            Console.WriteLine("PASS: " + checks + " server checks");
        }
        finally { File.Delete(path); }
    }
}
