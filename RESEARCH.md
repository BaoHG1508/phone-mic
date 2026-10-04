# How "phone as PC microphone" apps work — research notes (September 2026)

## The common architecture

Every product in this space has the same three parts:

1. **A phone app** that captures the microphone (Android `AudioRecord`, typically 48 kHz / 16-bit)
   and pushes it over a link: Wi-Fi (UDP or TCP), USB, or Bluetooth.
2. **A PC program** that receives the stream, buffers it against network jitter, and plays it.
3. **A virtual microphone on Windows.** Windows programs (Discord, Zoom, OBS, games) can only use
   *recording devices* that a kernel-mode audio driver exposes. The receiver therefore plays the
   audio into a **virtual audio device** whose playback endpoint is wired to a recording endpoint.
   Two approaches exist:
   * ship a **custom signed driver** ("WO Mic Virtual Device", "Virtual Mic for AudioRelay",
     "DroidCam Virtual Audio") — best UX, but requires kernel-driver development and an EV code
     signing certificate, plus a reboot on install;
   * rely on a **general-purpose virtual cable** such as VB-Audio's **VB-CABLE** (donationware,
     creates `CABLE Input` playback + `CABLE Output` recording; everything sent to the input
     appears on the output) — this is what the open-source AndroidMic does, and what PhoneMic does.

## Products compared

| App | Transports | Audio format / codec | Latency claims | Windows side | Price |
|-----|-----------|----------------------|----------------|--------------|-------|
| **AudioRelay** (`com.azefsw.audioconnect`) | Wi-Fi, USB (via USB tethering) | Opus or uncompressed PCM, 16-bit, stereo capable; adjustable buffers | ~20-30 ms on good 5 GHz Wi-Fi; "virtually 0 ms" claim over USB tethering | Own app + own "Virtual Mic for AudioRelay" device. PC side is the *player* that connects to the phone's *server* (phone is listed under "servers"). Also streams PC audio *to* the phone; Windows/macOS/Linux | Free tier with Premium upgrade |
| **WO Mic** (`com.wo.voice2`) | USB, Wi-Fi, Wi-Fi Direct, Bluetooth (Android); iOS Wi-Fi only | 48 kHz, 16-bit, **mono** PCM | "very short lag" | Windows client + kernel virtual mic driver; client connects to the phone (phone shows its IP) | Free |
| **Micstream** (`com.totech.phoneasmic`) | USB, Wi-Fi | 48 kHz, up to 32-bit float, stereo; Opus over Wi-Fi | "often under 10 ms" over USB | Own Windows driver presenting a *real input device*; automatic PC discovery | Free with ads / hourly limits; Pro subscription |
| **DroidCam** (webcam app, mic optional) | Wi-Fi, USB (adb) | voice-oriented | variable; audio is secondary to video | "DroidCam Audio" virtual device installed with the video driver | Free; Pro for HD |
| **Phone As Mic** (`com.phoneasmic.phone_as_mic`) | Wi-Fi, Bluetooth, USB | — | — | Windows companion app, or works as a browser source in OBS (no driver) | Free/Pro |
| **AndroidMic** (open source, F-Droid/GitHub `teamclouday/AndroidMic`) | TCP, UDP, USB serial, USB ADB | 44.1/48 kHz, 16/24-bit, mono/stereo; optional RNNoise noise cancellation | — | Rust desktop app (Windows/macOS/Linux) that outputs to a **virtual cable you install yourself** (VB-CABLE) | Free (open source) |

## Take-aways that shaped PhoneMic

* **Transport**: UDP for Wi-Fi (no head-of-line blocking; a lost packet is a 10 ms blip, not a
  stall) plus TCP for USB — `adb reverse` only forwards TCP, which is exactly how AndroidMic's
  "USB ADB" mode and DroidCam's USB mode work. WO Mic's Bluetooth mode was dropped: Bluetooth
  SCO/RFCOMM caps quality at 8-16 kHz and adds far more latency than Wi-Fi.
* **Codec**: uncompressed 16-bit PCM like WO Mic. On a LAN the 0.8 Mbit/s of 48 kHz mono is
  negligible, decoding is trivial, and there is zero codec delay. Opus (AudioRelay/Micstream) only
  pays off on congested Wi-Fi; it is a natural follow-up (Android has a MediaCodec Opus encoder).
* **Frame size**: 10 ms packets at 48 kHz (960 bytes) — under the MTU, and small enough that the
  end-to-end delay is dominated by the receiver's jitter buffer (default 40 ms) rather than
  packetisation.
* **Direction of connection**: AudioRelay makes the PC connect to the phone; WO Mic does the
  same. PhoneMic does the opposite (phone sends to the PC's IP) because NAT/firewall rules on the
  phone side are unpredictable, and it adds **UDP broadcast discovery** so the user rarely has
  to type an IP (Micstream also auto-discovers).
* **Virtual mic**: PhoneMic uses **VB-CABLE** rather than a custom driver. Writing and signing a
  Windows kernel audio driver is out of scope for a first version; VB-CABLE is free, mature
  (Windows XP → 11, ARM64), and is the same approach AndroidMic ships with.
* **Android platform rules** that all current apps must follow and PhoneMic implements:
  a foreground service with `foregroundServiceType="microphone"` and the
  `FOREGROUND_SERVICE_MICROPHONE` permission (Android 14+), the service must be started while
  the app is visible, `POST_NOTIFICATIONS` runtime permission on Android 13+, and a partial
  wake lock plus a low-latency Wi-Fi lock so the stream survives the screen turning off.

## Sources

* AudioRelay — https://audiorelay.net/ , docs: https://audiorelay.net/docs/windows/use-your-phone-as-a-mic-for-windows ,
  Play Store: https://play.google.com/store/apps/details?id=com.azefsw.audioconnect
* WO Mic — https://wolicheng.com/womic/ , Play Store: https://play.google.com/store/apps/details?id=com.wo.voice2
* Micstream — https://micstream.io/ , comparison: https://micstream.io/guides/micstream-vs-audiorelay-vs-wo-mic
* AndroidMic (open source) — https://github.com/teamclouday/AndroidMic , https://f-droid.org/packages/io.github.teamclouday.AndroidMic/
* Phone As Mic — https://play.google.com/store/apps/details?id=com.phoneasmic.phone_as_mic
* DroidCam help — https://droidcam.app/help/ , OBS plugin: https://obsproject.com/forum/resources/droidcam-virtual-output.1580/
* VB-CABLE Virtual Audio Device — https://vb-audio.com/Cable/
