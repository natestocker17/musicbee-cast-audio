using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MusicBeePlugin
{
    // Keep MusicBee's transport running while preventing its local audio from
    // playing twice. Session mute leaves MusicBee's own volume and mute UI live.
    internal sealed class LocalAudioSilencer : IDisposable
    {
        private sealed class SavedSession
        {
            internal AudioSessionControl Control;
            internal bool WasMuted;
        }

        private readonly Plugin.MusicBeeApiInterface api;
        private readonly bool originalMusicBeeMute;
        private readonly Dictionary<string, SavedSession> sessions = new Dictionary<string, SavedSession>();
        private bool fallbackMute;
        private bool disposed;

        internal bool ChangingMusicBeeMute { get; private set; }
        internal DateTime IgnoreMuteNotificationsUntil { get; private set; }
        internal bool UsingFallbackMute { get { return fallbackMute; } }

        internal LocalAudioSilencer(Plugin.MusicBeeApiInterface api)
        {
            this.api = api;
            originalMusicBeeMute = api.Player_GetMute();
            EnsureMuted();
        }

        internal void EnsureMuted()
        {
            if (disposed) return;
            int activeSessions = 0;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    foreach (var device in endpoints)
                    {
                        try
                        {
                            var collection = device.AudioSessionManager.Sessions;
                            for (int i = 0; i < collection.Count; i++)
                            {
                                var session = collection[i];
                                try
                                {
                                    if (session.GetProcessID != Process.GetCurrentProcess().Id)
                                    {
                                        session.Dispose();
                                        continue;
                                    }
                                    var key = device.ID + "|" + session.GetSessionInstanceIdentifier;
                                    if (!sessions.ContainsKey(key))
                                    {
                                        var wasMuted = session.SimpleAudioVolume.Mute;
                                        session.SimpleAudioVolume.Mute = true;
                                        sessions.Add(key, new SavedSession
                                        {
                                            Control = session,
                                            WasMuted = wasMuted
                                        });
                                    }
                                    else
                                    {
                                        session.SimpleAudioVolume.Mute = true;
                                        session.Dispose();
                                    }
                                    activeSessions++;
                                }
                                catch
                                {
                                    try { session.Dispose(); } catch { }
                                }
                            }
                        }
                        finally { device.Dispose(); }
                    }
                }
            }
            catch { /* MusicBee mute below is the fallback for unavailable sessions. */ }

            // A session may appear after a track or output device change. Keep
            // MusicBee muted until a session has been captured and muted.
            if (activeSessions == 0)
            {
                fallbackMute = true;
                SetMusicBeeMute(true);
            }
            else if (fallbackMute)
            {
                fallbackMute = false;
                SetMusicBeeMute(originalMusicBeeMute);
            }
        }

        internal void RestoreFallbackMute()
        {
            if (fallbackMute) SetMusicBeeMute(true);
        }

        private void SetMusicBeeMute(bool mute)
        {
            if (api.Player_GetMute() == mute) return;
            ChangingMusicBeeMute = true;
            IgnoreMuteNotificationsUntil = DateTime.UtcNow.AddMilliseconds(400);
            try { api.Player_SetMute(mute); }
            finally { ChangingMusicBeeMute = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var saved in sessions.Values)
            {
                try { saved.Control.SimpleAudioVolume.Mute = saved.WasMuted; }
                catch { }
                saved.Control.Dispose();
            }
            sessions.Clear();
            SetMusicBeeMute(originalMusicBeeMute);
        }
    }
}
