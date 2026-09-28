using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        static Plugin() { DependencyResolver.Install(); }

        private MusicBeeApiInterface api;
        private readonly PluginInfo info = new PluginInfo();
        private CastWindow window;
        private ChromecastClient client;
        private ChromecastReceiver receiver;
        private LocalMediaServer server;
        private LocalAudioSilencer silencer;
        private bool casting;
        private bool busy;
        private bool closing;
        private bool castMuted;
        private string lastFile;
        private readonly SemaphoreSlim commandGate = new SemaphoreSlim(1, 1);
        private readonly System.Windows.Forms.Timer positionTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly SeekDetector seekDetector = new SeekDetector();
        private bool positionPollBusy;
        private int lastTrackPosition;
        private DateTime lastSilencerCheck;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            api = new MusicBeeApiInterface();
            api.Initialise(apiInterfacePtr);
            info.PluginInfoVersion = PluginInfoVersion;
            info.Type = PluginType.General;
            info.Name = "Cast Audio";
            info.Description = "Cast MusicBee tracks to a Chromecast Audio";
            info.Author = "Codex";
            info.TargetApplication = "";
            info.VersionMajor = 1;
            info.VersionMinor = 1;
            info.Revision = 1;
            info.MinInterfaceVersion = MinInterfaceVersion;
            info.MinApiRevision = MinApiRevision;
            info.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            info.ConfigurationPanelHeight = 0;
            positionTimer.Tick += async (s, e) => await PollPositionAsync();
            return info;
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type == NotificationType.PluginStartup)
            {
                api.MB_AddMenuItem("mnuTools/Cast Audio...", "Cast Audio...", (s, e) => ShowWindow());
                api.MB_RegisterCommand("Cast Audio: Open", (s, e) => ShowWindow());
            }
            else if (casting && !closing)
            {
                switch (type)
                {
                    case NotificationType.TrackChanged:
                        RunOnUi(async () => await CastCurrentTrackAsync(true));
                        break;
                    case NotificationType.PlayStateChanged:
                        RunOnUi(async () => await SyncPlayStateAsync());
                        break;
                    case NotificationType.VolumeLevelChanged:
                        RunOnUi(async () => await SyncVolumeAsync());
                        break;
                    case NotificationType.VolumeMuteChanged:
                        RunOnUi(async () => await SyncMuteAsync());
                        break;
                }
            }
        }

        public void Close(PluginCloseReason reason)
        {
            closing = true;
            casting = false;
            positionTimer.Stop();
            try { server?.Dispose(); } catch { }
            server = null;
            try { silencer?.Dispose(); } catch { }
            silencer = null;
            if (window != null && !window.IsDisposed) window.Close();
        }

        private void RunOnUi(Action action)
        {
            try
            {
                var control = Control.FromHandle(api.MB_GetWindowHandle());
                if (control != null && control.IsHandleCreated)
                    control.BeginInvoke((MethodInvoker)(() => action()));
                else if (window != null && window.IsHandleCreated && !window.IsDisposed)
                    window.BeginInvoke((MethodInvoker)(() => action()));
            }
            catch { }
        }

        private void ShowWindow()
        {
            if (closing) return;
            if (window == null || window.IsDisposed)
            {
                window = new CastWindow();
                window.RefreshClicked += async (s, e) => await DiscoverAsync();
                window.CastClicked += async (s, e) => await ConnectAndCastAsync();
                window.StopClicked += async (s, e) => await StopAsync();
                window.FormClosed += (s, e) => window = null;
                window.Show();
                _ = DiscoverAsync();
            }
            else { window.BringToFront(); window.Activate(); }
        }

        private async Task DiscoverAsync()
        {
            if (window == null || window.IsDisposed) return;
            window.Status = "Searching for Cast devices...";
            try
            {
                using (var locator = new ChromecastLocator())
                {
                    var devices = await locator.FindReceiversAsync();
                    if (window == null || window.IsDisposed) return;
                    window.SetDevices(devices.ToList());
                    window.Status = devices.Any() ? "Select a device and press Cast." : "No devices found. Enter its IP address below.";
                }
            }
            catch (Exception ex) { SetError("Discovery failed", ex); }
        }

        private async Task ConnectAndCastAsync()
        {
            if (busy || closing) return;
            var file = CurrentLocalFile();
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                if (window != null) window.Status = "Play a local file in MusicBee first.";
                return;
            }
            try { AudioFormat.ContentType(file); }
            catch (NotSupportedException ex) { if (window != null) window.Status = ex.Message; return; }

            var selected = window.SelectedDevice;
            if (!string.IsNullOrWhiteSpace(window.ManualAddress))
            {
                IPAddress address;
                if (!IPAddress.TryParse(window.ManualAddress, out address) || address.AddressFamily != AddressFamily.InterNetwork)
                { window.Status = "Enter a valid IPv4 address."; return; }
                selected = new ChromecastReceiver { Name = address.ToString(), DeviceUri = new Uri("http://" + address), Port = 8009 };
            }
            if (selected == null) { window.Status = "Select a device or enter its IPv4 address."; return; }

            busy = true;
            try
            {
                if (casting) await StopAsync();
                window.Status = "Connecting to " + selected.Name + "...";
                var nextClient = new ChromecastClient();
                await nextClient.ConnectChromecast(selected);
                await nextClient.LaunchApplicationAsync("CC1AD845");
                client = nextClient;
                receiver = selected;
                server = new LocalMediaServer();
                castMuted = api.Player_GetMute();
                silencer = new LocalAudioSilencer(api);
                casting = true;
                lastFile = null;
                positionTimer.Start();
                await CastCurrentTrackAsync(true);
                if (casting && silencer.UsingFallbackMute && window != null)
                    window.Status = "Casting to " + selected.Name + ". Local audio uses MusicBee mute for this output mode.";
            }
            catch (Exception ex)
            {
                await StopAsync();
                SetError("Cast failed", ex);
            }
            finally { busy = false; }
        }

        private async Task CastCurrentTrackAsync(bool force)
        {
            if (!casting || client == null || server == null || closing) return;
            Exception error = null;
            await commandGate.WaitAsync();
            try
            {
                if (casting) await CastCurrentTrackCoreAsync(force);
            }
            catch (Exception ex) { error = ex; }
            finally { commandGate.Release(); }
            if (error != null)
            {
                await StopAsync();
                SetError("Could not cast this track", error);
            }
        }

        private async Task CastCurrentTrackCoreAsync(bool force)
        {
            var file = CurrentLocalFile();
            if (file == lastFile)
            {
                if (!force || api.Player_GetPosition() + 500 >= lastTrackPosition) return;
            }
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                throw new IOException("The current track is not a local file.");

            var mime = AudioFormat.ContentType(file);
            IPAddress receiverIp;
            if (!IPAddress.TryParse(receiver.DeviceUri.Host, out receiverIp))
                receiverIp = Dns.GetHostAddresses(receiver.DeviceUri.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            IPAddress localIp;
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect(receiverIp, 8009);
                localIp = ((IPEndPoint)socket.LocalEndPoint).Address;
            }
            var url = server.Publish(file, localIp);
            var media = new Media
            {
                ContentUrl = url,
                ContentType = mime,
                Metadata = new MusicTrackMetadata
                {
                    Title = api.NowPlaying_GetFileTag(MetaDataType.TrackTitle) ?? Path.GetFileNameWithoutExtension(file),
                    Artist = api.NowPlaying_GetFileTag(MetaDataType.Artist),
                    MetadataType = MetadataType.Music
                }
            };
            if (window != null) window.Status = "Loading " + Path.GetFileName(file) + "...";
            // A normal track change starts at zero. Let the receiver play as soon
            // as it has buffered the file instead of waiting for volume, mute,
            // seek and play round trips before any sound is heard.
            var joinMidTrack = lastFile == null && api.Player_GetPosition() > 1500;
            var autoPlay = api.Player_GetPlayState() == PlayState.Playing && !joinMidTrack;
            await client.MediaChannel.LoadAsync(media, autoPlay, null);
            if (!casting) return;
            lastFile = file;
            await client.MediaChannel.SetVolumeAsync(ClampVolume(api.Player_GetVolume()));
            await client.MediaChannel.SetMuteAsync(castMuted);
            if (joinMidTrack)
            {
                var position = api.Player_GetPosition();
                if (position > 1500) await client.MediaChannel.SeekAsync(position / 1000.0);
            }
            if (!autoPlay && api.Player_GetPlayState() == PlayState.Playing)
                await client.MediaChannel.PlayAsync();
            else if (autoPlay && api.Player_GetPlayState() == PlayState.Paused)
                await client.MediaChannel.PauseAsync();
            ResetPositionSample();
            if (window != null) window.Status = "Casting to " + receiver.Name + ": " + Path.GetFileName(file);
        }

        private static double ClampVolume(float value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        private string CurrentLocalFile()
        {
            var file = api.NowPlaying_GetFileUrl();
            Uri uri;
            if (Uri.TryCreate(file, UriKind.Absolute, out uri) && uri.IsFile) return uri.LocalPath;
            return file;
        }

        private async Task SyncPlayStateAsync()
        {
            if (!casting || client == null) return;
            var state = api.Player_GetPlayState();
            try
            {
                await commandGate.WaitAsync();
                try
                {
                    if (!casting) return;
                    if (state == PlayState.Playing)
                    {
                        if (lastFile == null) await CastCurrentTrackCoreAsync(true);
                        else await client.MediaChannel.PlayAsync();
                    }
                    else if (state == PlayState.Paused && lastFile != null)
                        await client.MediaChannel.PauseAsync();
                    else if (state == PlayState.Stopped && lastFile != null)
                    {
                        await client.MediaChannel.StopAsync();
                        lastFile = null;
                    }
                    ResetPositionSample();
                }
                finally { commandGate.Release(); }
            }
            catch (Exception ex)
            {
                await StopAsync();
                SetError("Cast playback control failed", ex);
            }
        }

        private async Task SyncVolumeAsync()
        {
            if (!casting || client == null) return;
            try
            {
                await commandGate.WaitAsync();
                try { if (casting) await client.MediaChannel.SetVolumeAsync(ClampVolume(api.Player_GetVolume())); }
                finally { commandGate.Release(); }
            }
            catch (Exception ex) { SetError("Cast volume control failed", ex); }
        }

        private async Task SyncMuteAsync()
        {
            if (!casting || client == null || silencer == null || silencer.ChangingMusicBeeMute ||
                DateTime.UtcNow < silencer.IgnoreMuteNotificationsUntil) return;
            if (silencer.UsingFallbackMute)
            {
                if (api.Player_GetMute()) return;
                castMuted = !castMuted;
                silencer.RestoreFallbackMute();
            }
            else castMuted = api.Player_GetMute();
            try
            {
                await commandGate.WaitAsync();
                try { if (casting) await client.MediaChannel.SetMuteAsync(castMuted); }
                finally { commandGate.Release(); }
            }
            catch (Exception ex) { SetError("Cast mute control failed", ex); }
        }

        private void ResetPositionSample()
        {
            lastTrackPosition = api.Player_GetPosition();
            seekDetector.Reset(lastTrackPosition, api.Player_GetPlayState() == PlayState.Playing, DateTime.UtcNow);
        }

        private async Task PollPositionAsync()
        {
            if (positionPollBusy || !casting || closing) return;
            positionPollBusy = true;
            try
            {
                var now = DateTime.UtcNow;
                if ((now - lastSilencerCheck).TotalSeconds >= 2)
                {
                    silencer?.EnsureMuted();
                    lastSilencerCheck = now;
                }
                if (lastFile == null || commandGate.CurrentCount == 0) return;
                if (!string.Equals(CurrentLocalFile(), lastFile, StringComparison.OrdinalIgnoreCase)) return;
                var state = api.Player_GetPlayState();
                var position = api.Player_GetPosition();
                if (position + 500 >= lastTrackPosition) lastTrackPosition = position;
                if (state == PlayState.Playing || state == PlayState.Paused)
                {
                    if (seekDetector.ShouldSeek(position, state == PlayState.Playing, now))
                    {
                        await commandGate.WaitAsync();
                        try
                        {
                            if (casting && lastFile != null &&
                                string.Equals(CurrentLocalFile(), lastFile, StringComparison.OrdinalIgnoreCase))
                            {
                                await client.MediaChannel.SeekAsync(Math.Max(0, api.Player_GetPosition() / 1000.0));
                                ResetPositionSample();
                            }
                        }
                        finally { commandGate.Release(); }
                    }
                }
            }
            catch (Exception ex)
            {
                await StopAsync();
                SetError("Cast seek failed", ex);
            }
            finally { positionPollBusy = false; }
        }

        private async Task StopAsync()
        {
            if (!casting && client == null && silencer == null) return;
            casting = false;
            positionTimer.Stop();
            await commandGate.WaitAsync();
            try
            {
                try { if (client != null) await client.MediaChannel.StopAsync(); }
                catch (Exception ex) { SetError("Could not stop receiver", ex); }
                client = null;
                receiver = null;
                lastFile = null;
                try { server?.Dispose(); } catch { }
                server = null;
                try { silencer?.Dispose(); } catch { }
                silencer = null;
                lastTrackPosition = 0;
                if (window != null) window.Status = "Casting stopped.";
            }
            finally { commandGate.Release(); }
        }

        private void SetError(string context, Exception ex)
        {
            if (window != null && !window.IsDisposed) window.Status = context + ": " + ex.Message;
        }
    }
}
