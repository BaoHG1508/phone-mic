# PhoneMic wire protocol (version 1)

One small binary packet format is used for everything: audio, discovery and keep-alives.
The Android app (`Pmic.kt`) and the PC receiver (`phonemic_receiver.py`) implement it, and both
test suites check the same byte vector so they cannot drift apart.

## Packet layout

All multi-byte integers in the header are **big-endian**. PCM samples in the payload are
**little-endian signed 16-bit**, interleaved when stereo (the native Android / WAV layout).

| Offset | Size | Field           | Notes                                                   |
|-------:|-----:|-----------------|---------------------------------------------------------|
| 0      | 2    | magic           | ASCII `P` `M` (`0x50 0x4D`)                              |
| 2      | 1    | version         | `1`                                                     |
| 3      | 1    | type            | see table below                                         |
| 4      | 4    | seq             | uint32; +1 per AUDIO packet (wraps), `0` for control    |
| 8      | 4    | sampleRate      | Hz, e.g. `48000`; `0` for control packets               |
| 12     | 1    | channels        | `1` or `2`; `0` for control packets                     |
| 13     | 1    | bitsPerSample   | `16`; `0` for control packets                           |
| 14     | 2    | payloadLen      | bytes that follow the 16-byte header                    |
| 16     | n    | payload         | PCM for AUDIO, UTF-8 text for control packets           |

Types:

| Value | Name     | Direction  | Payload                                        |
|------:|----------|------------|------------------------------------------------|
| 1     | AUDIO    | phone → PC | PCM frame                                      |
| 2     | DISCOVER | phone → PC | phone model (informational), sent to broadcast |
| 3     | ANNOUNCE | PC → phone | PC host name                                   |
| 4     | PING     | phone → PC | optional text; sent once per second            |
| 5     | PONG     | PC → phone | PC host name                                   |
| 6     | BYE      | phone → PC | none; sent when the user stops                 |

Example AUDIO packet, `seq = 258`, 48 kHz mono, two samples (`1`, `32767`):

```
50 4d 01 01  00 00 01 02  00 00 bb 80  01 10 00 04  01 00 ff 7f
```

## Frame size

The app sends the largest of 20 / 10 / 5 ms whose PCM payload stays ≤ 1200 bytes, so one packet
always fits a 1500-byte MTU without IP fragmentation:

| Format         | Frame | Payload | Packets/s | Bitrate    |
|----------------|------:|--------:|----------:|-----------:|
| 16 kHz mono    | 20 ms | 640 B   | 50        | 262 kbit/s |
| 24 kHz mono    | 20 ms | 960 B   | 50        | 390 kbit/s |
| 44.1 kHz mono  | 10 ms | 882 B   | 100       | 718 kbit/s |
| 48 kHz mono    | 10 ms | 960 B   | 100       | 781 kbit/s |
| 48 kHz stereo  | 5 ms  | 960 B   | 200       | 1.56 Mbit/s |

Uncompressed PCM is deliberate: it is trivial to decode, adds zero codec latency, and even the
stereo case is far below what any Wi-Fi link or USB cable carries. (AudioRelay and Micstream use
Opus to save bandwidth; WO Mic sends PCM like we do.)

## Transports

* **UDP** (default, port 48111): one packet per datagram. Lost packets are concealed with silence
  by the receiver; late/duplicate packets are dropped. The phone marks the DSCP field `EF`
  (`0xB8`) so Wi-Fi QoS can prioritise the stream.
* **TCP** (same port): packets are simply concatenated; `payloadLen` delimits them. Used for USB
  (via `adb reverse`, which only forwards TCP) and as a fallback where UDP is blocked.
  `TCP_NODELAY` is set on both ends.

The receiver listens on both at once, so the phone can switch transports without any PC-side
change.

## Discovery and liveness

1. The phone sends DISCOVER to every interface's broadcast address (and `255.255.255.255`) on the
   configured port, three times over ~2.5 s.
2. Each PC receiver answers with a unicast ANNOUNCE carrying its host name.
3. While streaming, the phone sends a PING every second on the audio socket/connection; the
   receiver answers PONG. If no PONG arrives for 3 s the app shows "PC receiver not responding"
   (over UDP there is otherwise no way to know the PC is there).

## Receiver behaviour

* Jitter buffer pre-fills `--buffer-ms` (default 40 ms) before playback starts; on an underrun it
  outputs silence and re-fills.
* Audio older than `--max-buffer-ms` (default 250 ms) is discarded so latency cannot creep up.
* If the output device cannot run at the phone's sample rate or channel count, the receiver opens
  the device at its default rate and resamples (linear interpolation) / up- or down-mixes.
* A change of sample rate or channels in the incoming stream reopens the output device.
