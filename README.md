# MusicBee Cast Audio 1.1.0

Cast the current MusicBee track to a Chromecast Audio or another Google Cast receiver. Download the ready-to-install ZIP from the [latest GitHub release](https://github.com/natestocker17/musicbee-cast-audio/releases/latest).

## Install

1. Close MusicBee.
2. Extract **all DLL files** from `mb_CastAudio-1.1.0.zip` directly into your MusicBee `Plugins` folder. Keep the dependency DLLs beside `mb_CastAudio.dll`.
3. For the Microsoft Store version of MusicBee, the folder is usually:
   `%LOCALAPPDATA%\Packages\50072StevenMayall.MusicBee_kcr266et74avj\LocalCache\Roaming\MusicBee\Plugins`
4. Restart MusicBee. Check **Preferences → Plugins** for **Cast Audio**.
5. Play a local track. Open **Tools → Cast Audio...**, select a receiver, and press **Cast**. If discovery fails, enter its IPv4 address.

If you already use another Chromecast plugin, avoid running both against the same receiver at once.

## Controls and behavior

- The Cast window chooses and disconnects the receiver. Use MusicBee's own play/pause, previous/next, seek bar, volume and mute controls during casting.
- MusicBee keeps playing silently on the PC so its native playback bar, queue, shuffle, repeat, and automatic next track continue to work. The plugin relays its state to the receiver.
- The plugin first mutes only MusicBee's Windows audio session, preserving MusicBee's native mute indicator and volume control. If no session is available, it temporarily uses MusicBee mute; in that case the mute icon stays on while casting. Each click on that icon toggles the receiver mute, and the icon returns to muted to keep the PC silent.
- Disconnect restores the MusicBee mute state and any Windows audio session mute state captured when casting began. MusicBee keeps its current play/pause state.
- Closing the Cast window leaves casting active; reopen it from Tools to change device or disconnect.
- Closing MusicBee ends the local media server, so the receiver cannot continue fetching the track.

The plugin serves the audio file from your PC on a random LAN TCP port, protected by a fresh random URL token for each track. The Chromecast must be able to reach your PC over the same network. Windows Firewall may prompt to allow MusicBee on private networks. MusicBee's seek bar is polled twice per second, so a seek may take about half a second to reach the receiver. Playback may drift slightly because the PC and receiver each decode the file independently. Windows session mute does not silence WASAPI exclusive-mode output; use a shared output mode when casting if you hear local audio.

Supported local file extensions: MP3, M4A/MP4, FLAC, WAV, OGG/OGA, Opus, WebM. Actual codec support depends on the receiver. The plugin does not transcode, and it does not cast MusicBee web streams, protected audio, or MusicBee's DSP output.

## Build and verification

Build with `dotnet build mb_CastAudio.csproj -c Release`. Requires the .NET SDK and NuGet restore. Targets x86 .NET Framework 4.8 for MusicBee 3.x. `tests/ServerTests.csproj` checks the local HTTP server and Cast SDK load.

Verified on 26 September 2026: Release build and x86 server/runtime checks, plus discovery of six Cast devices from an x86 host without application binding redirects. Version 1.1.0 resolves patch-versioned dependencies inside the plugin because MusicBee owns its executable configuration. In a live test, MusicBee play/pause, seeking, Next, and volume worked on a Chromecast receiver while the PC stayed silent. Previous, mute, and long-term queue playback were not checked in that test.

## Source and dependencies

Source is in this repository and supplied in `MusicBeeChromecastAudio-1.1.0-source.zip`. The MusicBee interface declaration follows the MusicBee plugin SDK. Cast communication uses SharpCaster 3.0.0. Windows audio session control uses NAudio.Wasapi 2.2.1. See `THIRD_PARTY_NOTICES.txt` for dependency versions and licenses.
