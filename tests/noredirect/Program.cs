using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections;

class Program
{
    static void Main(string[] args)
    {
        var plugin = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mb_CastAudio.dll"));
        Activator.CreateInstance(plugin.GetType("MusicBeePlugin.Plugin", true));
        foreach (var name in new[] { "System.Text.Json", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Logging.Abstractions" })
        {
            var actual = AssemblyName.GetAssemblyName(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".dll"));
            var request = new AssemblyName(actual.FullName) { Version = new Version(9, 0, 0, 0) };
            var loaded = Assembly.Load(request);
            if (loaded == null || loaded.GetName().Name != name) throw new Exception("Could not resolve " + name);
            Console.WriteLine(name + ": requested " + request.Version + ", loaded " + loaded.GetName().Version);
        }
        var audioAssembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NAudio.Wasapi.dll"));
        var enumeratorType = audioAssembly.GetType("NAudio.CoreAudioApi.MMDeviceEnumerator", true);
        using (var enumerator = (IDisposable)Activator.CreateInstance(enumeratorType))
        {
            var flow = Enum.Parse(audioAssembly.GetType("NAudio.CoreAudioApi.DataFlow", true), "Render");
            var state = Enum.Parse(audioAssembly.GetType("NAudio.CoreAudioApi.DeviceState", true), "Active");
            var endpoints = enumeratorType.GetMethod("EnumerateAudioEndPoints").Invoke(enumerator, new[] { flow, state });
            var endpointCount = (int)endpoints.GetType().GetProperty("Count").GetValue(endpoints);
            Console.WriteLine("NAudio active render endpoints: " + endpointCount);
        }
        var castAssembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sharpcaster.dll"));
        var locator = Activator.CreateInstance(castAssembly.GetType("Sharpcaster.ChromecastLocator", true), new object[] { null });
        var search = locator.GetType().GetMethod("FindReceiversAsync").Invoke(locator, new object[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) });
        ((Task)search).GetAwaiter().GetResult();
        var receivers = (IEnumerable)search.GetType().GetProperty("Result").GetValue(search);
        int count = 0;
        foreach (var device in receivers) count++;
        ((IDisposable)locator).Dispose();
        Console.WriteLine("PASS: no-redirect x86 host discovered " + count + " Cast devices");
    }
}
