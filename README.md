# PhoneMic — use your Android phone as a microphone for your Windows PC

PhoneMic streams your phone's microphone to your PC over Wi-Fi or a USB cable, the same way
AudioRelay, WO Mic and Micstream do. It has two parts:

| Part | Folder | What it is |
|------|--------|------------|
| Android app | [`android/`](android/) | Kotlin app: captures the mic in a foreground service and sends 16-bit PCM over UDP or TCP. Auto-discovers the PC, shows a level meter, keeps running with the screen off. |
| Windows receiver app | [`pc-receiver-windows/`](pc-receiver-windows/) | Native Windows program (`PhoneMicReceiver.exe`, C#/WinForms + NAudio): receives the stream, smooths network jitter and plays it into a virtual audio cable so Windows apps see a microphone. Tray icon, auto-start, device picker, firewall helper. |
| Python receiver | [`pc-receiver/`](pc-receiver/) | The same receiver as a small Python script, for macOS/Linux or if you prefer a console tool. |

Background research on how the existing apps work is in [RESEARCH.md](RESEARCH.md); the wire
format is documented in [PROTOCOL.md](PROTOCOL.md).

## Download

Prebuilt files are attached to each release on the
[Releases page](../../releases/latest):

| File | For |
|------|-----|
| `PhoneMicReceiver-<version>.exe` | Windows 10/11 (x64). Single file, no installer, no .NET runtime needed. |
| `PhoneMic-<version>.apk` | Android 7.0 or newer. |

The Windows executable is not code-signed, so SmartScreen shows "Windows protected your PC" on
the first run. Choose *More info*, then *Run anyway*. You can also build both yourself; see below.

## Quick start

### 1. PC (Windows 10/11) — once

1. Install **VB-CABLE** from https://vb-audio.com/Cable/ (free/donationware). Run the installer
   as administrator and reboot. It adds a playback device `CABLE Input` and a recording device
   `CABLE Output`; whatever is played into the first comes out of the second.
2. Download `PhoneMicReceiver.exe` from the [Releases page](../../releases/latest) and run it
   (no installation, no .NET runtime needed; it is a single self-contained file). It selects `CABLE Input` automatically,
   starts listening on port 48111 and sits in the tray. Allow the Windows Firewall prompt
   (private networks) the first time, or click **Allow through Windows Firewall…** in the app.
3. In Windows *Settings → System → Sound → Input* (or inside Discord/Zoom/OBS) choose
   **CABLE Output** as the microphone.

Without VB-CABLE the receiver still works but plays the phone through your speakers, which is
handy for a first test.

**Alternative: Python receiver** (also works on macOS/Linux). Install Python 3.9+ (tick *Add
python.exe to PATH*) and double-click [`pc-receiver/run_receiver.bat`](pc-receiver/run_receiver.bat);
the first run installs `sounddevice` + `numpy`. It prints:

```
[audio] using virtual cable: [7] CABLE Input (VB-Audio Virtual Cable)
[net] listening on UDP+TCP port 48111 as 'DESKTOP-ABC123'. Press Ctrl+C to quit.
```

### 2. Phone

1. Install the app: download the APK from the [Releases page](../../releases/latest) and open
   it on the phone, or run `adb install -r PhoneMic-<version>.apk`, or build it yourself (below).
2. Connect the phone to the **same Wi-Fi network** as the PC.
3. Open PhoneMic → **Find PC** → tap your computer (or type its IP address).
4. Press **Start microphone**, allow the microphone/notification permissions. The status line
   turns green: *Streaming to 192.168.x.x:48111*. Talk — the level meter moves and the receiver
   window prints `[stats] ... 100 pkt/s | 781 kbit/s | buffer 40 ms | lost 0 ...`.
5. Lock the phone or switch apps; streaming continues (notification with Mute/Stop buttons).

### USB cable instead of Wi-Fi (lowest latency, no network needed)

`adb reverse` forwards a port from the phone to the PC over USB, so no Wi-Fi is involved.

1. Enable *Developer options → USB debugging* on the phone and plug it in.
2. On the PC (with [platform-tools](https://developer.android.com/tools/releases/platform-tools)
   on the PATH) run:

   ```bash
   adb reverse tcp:48111 tcp:48111
   ```

3. In the app choose **USB cable (TCP via adb reverse)** and press Start. The app shows the exact
   command with a Copy button.

## Settings explained

| Setting | Default | Notes |
|---------|---------|-------|
| Wi-Fi (UDP) | ✔ | Lowest latency. Lost packets become 10 ms of silence. |
| Wi-Fi (TCP) | | Use if your network blocks UDP; slightly higher latency under loss. |
| USB (TCP via adb) | | Needs `adb reverse`; unaffected by Wi-Fi quality. |
| Sample rate | 48 kHz | 16 kHz is plenty for voice chat and uses a third of the bandwidth. |
| Channels | Mono | Stereo doubles the bandwidth; most phone mics are mono anyway. |
| Microphone source | Default | *Voice call* enables the phone's own echo/noise processing; *Unprocessed* is the raw capsule. |
| Noise suppression / AGC | on / off | Android's built-in effects (if the phone offers them). |

Windows app settings: output device, port, jitter buffer (ms), *start listening when the app
opens*, *keep running in the tray*. They are saved in `%AppData%\PhoneMic\settings.json`.

Python receiver options (`run_receiver.bat --help`):

```
--device "CABLE Input"     output device (index or part of its name); default: VB-CABLE if installed
--buffer-ms 40             jitter buffer; raise to 80-120 on flaky Wi-Fi, lower to 20 on USB
--max-buffer-ms 250        latency cap; older audio is dropped
--record take.wav          also save the incoming audio
--list-devices             show all audio devices
--port 48111               must match the app
```

## Latency

End-to-end delay is roughly: 10 ms capture frame + network (1-5 ms Wi-Fi, <1 ms USB) +
jitter buffer (40 ms default) + the output device's own buffer (VB-CABLE ~10-20 ms). Expect
60-80 ms over Wi-Fi with defaults and about 40 ms over USB with `--buffer-ms 20`. That is
comparable to AudioRelay's Wi-Fi mode and well inside what voice chat tolerates.

## Troubleshooting

* **"PC receiver not responding"** in the app while streaming — the PC did not answer the keep-alive.
  Check the receiver is running, the firewall allows Python on UDP/TCP 48111, and both devices
  are on the same network (guest Wi-Fi networks often isolate clients).
* **Find PC finds nothing** — same causes as above; broadcast discovery does not cross routers or
  the Android emulator's NAT. Type the PC's IP (`ipconfig` on Windows) manually.
* **Choppy audio / `underruns` climbing** — raise `--buffer-ms`, move closer to the router, prefer
  5 GHz Wi-Fi or use USB. `lost` counts UDP packets that never arrived.
* **`trims` climbing** — the PC's audio device is not consuming audio as fast as it arrives
  (another app grabbed the device exclusively, or the device is asleep). Reopen the receiver.
* **No sound in Discord/Zoom** — pick *CABLE Output* as the input device in that app, and make
  sure the receiver printed `using virtual cable`.
* **USB mode shows "Reconnecting to PC over USB…"** — adb accepts the phone's connection even when
  nothing is listening on the PC, then drops it. Start the receiver, and re-run `adb reverse`
  after re-plugging the cable (the mapping is lost when the device disconnects).
* **App shows "Microphone is busy"** — another app is recording (a call, a voice recorder).
* **Streaming stops when the screen locks** — battery optimisation killed the service; exempt
  PhoneMic in *Settings → Battery* (Samsung: *Never sleeping apps*).

## What has been tested

On an Android 15 (API 35) emulator talking to the receiver on a Mac, driven through the real UI:

* Find PC discovers the receiver over UDP broadcast and fills in its address.
* Wi-Fi UDP streaming: 100 packets/s, 0 loss; keeps running with the screen off; mute/unmute;
  Stop sends BYE and the receiver logs it.
* Wi-Fi TCP: unplugging the receiver shows "Reconnecting…" within a few seconds and streaming
  resumes by itself when the receiver comes back.
* UDP liveness: without a receiver the status changes to "PC receiver not responding" after 3 s
  and clears when it returns.
* USB mode through `adb reverse`: streaming, receiver restart and recovery.
* Python receiver: UDP with simulated 2 % packet loss (concealed), TCP, mid-session format
  changes (48 kHz mono → 16 kHz mono → 48 kHz stereo), real audio-device playback, WAV
  recording, and the unit tests in `pc-receiver/tests`.
* Windows receiver: the shared core (protocol, jitter buffer, resampler, UDP/TCP server) passes
  17 unit tests including live socket round trips. The executable was cross-built from macOS and
  **has not yet been launched on a real Windows machine**, so the WinForms/NAudio layer is
  untested; please report anything odd in its log window.

Not yet tested on physical phones — please try it on yours and report which microphone source
sounds best.

## Building the Windows receiver

Requires the .NET SDK 7 or newer (it also builds on macOS/Linux thanks to `EnableWindowsTargeting`).
From `pc-receiver-windows/`:

```bash
./publish.sh
```

It runs the tests, publishes a compressed single-file executable to `dist/windows/`, and, when
building from macOS/Linux, flips the executable's PE subsystem from console to GUI (the SDK
cannot customise the Windows app host off-Windows, which would otherwise leave a console window
open behind the app).

`PhoneMic.Core` holds the protocol, jitter buffer, resampler and UDP/TCP server (unit-tested on any
OS); `PhoneMicReceiver` is the WinForms UI plus the NAudio (WASAPI, WaveOut fallback) output.

## Building the app

Requires JDK 17 and the Android SDK (platform 35). From `android/`:

```bash
./gradlew :app:assembleDebug
```

Unit tests (protocol codec, frame sizing, level meter):

```bash
./gradlew :app:testDebugUnitTest
```

Or open `android/` in Android Studio. `applicationId` is `com.phonemic.app`, `minSdk` 24
(Android 7.0), `targetSdk` 35.

PC receiver tests and a phone simulator (streams a test tone so you can verify VB-CABLE routing
without a phone):

```bash
cd pc-receiver
python -m unittest discover -s tests -v
python tools/fake_phone.py --host 127.0.0.1 --seconds 5
```

## How it works

```
 Android                                             Windows
 ┌──────────────────────────────────────┐            ┌─────────────────────────────────────┐
 │ AudioRecord 48 kHz PCM16 (10 ms)     │  UDP/TCP   │ phonemic_receiver.py                │
 │  → NoiseSuppressor/AGC (optional)    │ ─────────► │  → jitter buffer (40 ms, loss →     │
 │  → 16-byte header + PCM → queue      │  ◄─PONG──  │    silence, latency cap)            │
 │  → sender thread (UDP or TCP)        │            │  → sounddevice → "CABLE Input"      │
 │ foreground service, wake + Wi-Fi lock│            │      VB-CABLE → "CABLE Output" ──► Discord / OBS / Zoom
 └──────────────────────────────────────┘            └─────────────────────────────────────┘
```

Key Android files: `service/MicStreamService.kt` (foreground service, queue, reconnect,
keep-alive), `audio/AudioCapture.kt` (AudioRecord loop, effects, level meter),
`transport/UdpTransport.kt` + `TcpTransport.kt`, `net/PcDiscovery.kt`, `ui/MainActivity.kt`.

## Limitations / ideas

* Windows needs VB-CABLE; a dedicated signed virtual-microphone driver (what WO Mic and
  AudioRelay ship) would remove that step but requires kernel-driver development and code signing.
* No compression: 0.8 Mbit/s at 48 kHz mono. Opus via Android `MediaCodec` would cut that ~10×.
* One phone per receiver instance (a second phone's packets are mixed into the same stream).
* No Bluetooth transport (WO Mic has one); Bluetooth's audio profiles cap quality and add latency.

## Contributing

Issues and pull requests are welcome. The protocol is deliberately small and documented in
[PROTOCOL.md](PROTOCOL.md), so a receiver for another platform is a reasonable weekend project.
Please run the test suites before opening a pull request:

```bash
cd android && ./gradlew :app:testDebugUnitTest
cd pc-receiver-windows && dotnet test src/PhoneMic.Core.Tests
cd pc-receiver && python -m unittest discover -s tests
```

Releasing is described in [RELEASING.md](RELEASING.md).

## License

MIT, see [LICENSE](LICENSE). Third-party components and their licenses are listed in
[THIRD-PARTY.md](THIRD-PARTY.md). PhoneMic is not affiliated with VB-Audio Software, AudioRelay,
WO Mic, Micstream or DroidCam.
