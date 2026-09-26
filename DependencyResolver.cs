using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MusicBeePlugin
{
    // MusicBee owns MusicBee.exe.config, so a plugin cannot rely on NuGet's
    // executable binding redirects for patch-versioned .NET Framework DLLs.
    internal static class DependencyResolver
    {
        private static readonly HashSet<string> LocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Google.Protobuf", "Microsoft.Bcl.AsyncInterfaces",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions", "NAudio.Core", "NAudio.Wasapi", "Sharpcaster", "System.Buffers",
            "System.Diagnostics.DiagnosticSource", "System.IO.Pipelines", "System.Memory",
            "System.Numerics.Vectors", "System.Reactive", "System.Runtime.CompilerServices.Unsafe",
            "System.Text.Encodings.Web", "System.Text.Json", "System.Threading.Tasks.Extensions",
            "System.ValueTuple", "Zeroconf"
        };

        internal static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try { requested = new AssemblyName(args.Name); }
            catch { return null; }
            if (!LocalNames.Contains(requested.Name)) return null;

            var directory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            var path = Path.Combine(directory, requested.Name + ".dll");
            if (!File.Exists(path)) return null;
            try { return Assembly.LoadFrom(path); }
            catch (FileLoadException) { return null; }
            catch (BadImageFormatException) { return null; }
        }
    }
}
