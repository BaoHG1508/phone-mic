First public release.

**Android app** (`PhoneMic-1.0.0.apk`, Android 7.0 or newer)
- Streams the phone microphone as 16-bit PCM over Wi-Fi (UDP or TCP) or over a USB cable.
- Finds the PC automatically with UDP broadcast discovery.
- Foreground service keeps streaming with the screen off; mute and stop from the notification.
- Sample rate 16 to 48 kHz, mono or stereo, five microphone sources, noise suppression and AGC.
- Live level meter, bitrate and packet counters, automatic reconnect.

**Windows receiver** (`PhoneMicReceiver-1.0.0.exe`, Windows 10/11 x64)
- Single self-contained file. No installer and no .NET runtime needed.
- Selects VB-CABLE automatically so Windows apps see the phone as a microphone.
- Jitter buffer with packet-loss concealment and a latency cap.
- Tray icon, auto-start, device picker, Windows Firewall helper button.

**Also included in the source tree**: a Python receiver for macOS and Linux.

Requires [VB-CABLE](https://vb-audio.com/Cable/) on Windows so other programs can use the stream
as a microphone input. The executable is not code-signed, so SmartScreen warns on first run.
