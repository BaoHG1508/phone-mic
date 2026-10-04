#!/usr/bin/env python3
"""PhoneMic PC receiver.

Receives microphone audio from the PhoneMic Android app and plays it into an audio output
device. Point it at a virtual audio cable (VB-CABLE's "CABLE Input") and every Windows program
will see the phone as a normal microphone called "CABLE Output".

    python phonemic_receiver.py                      # auto-picks "CABLE Input" if installed
    python phonemic_receiver.py --list-devices       # show output devices
    python phonemic_receiver.py --device "CABLE Input" --buffer-ms 40
    python phonemic_receiver.py --record take1.wav   # also save what arrives

Protocol: see PROTOCOL.md next to this folder. Listens on UDP and TCP (same port, default 48111).
"""
from __future__ import annotations

import argparse
import os
import socket
import struct
import sys
import threading
import time
import wave
from dataclasses import dataclass, field
from typing import Optional

import numpy as np

# --------------------------------------------------------------------------- protocol

MAGIC = b"PM"
VERSION = 1
HEADER = struct.Struct("!2sBBIIBBH")  # magic, version, type, seq, rate, channels, bits, payloadLen
HEADER_SIZE = HEADER.size  # 16
DEFAULT_PORT = 48111
MAX_PACKET = 16 + 1400

TYPE_AUDIO = 1
TYPE_DISCOVER = 2
TYPE_ANNOUNCE = 3
TYPE_PING = 4
TYPE_PONG = 5
TYPE_BYE = 6

assert HEADER_SIZE == 16


@dataclass
class Packet:
    type: int
    seq: int
    sample_rate: int
    channels: int
    bits: int
    payload: bytes

    @property
    def text(self) -> str:
        return self.payload.decode("utf-8", "replace")


def pack(ptype: int, seq: int, rate: int, channels: int, bits: int, payload: bytes = b"") -> bytes:
    if len(payload) > 0xFFFF:
        raise ValueError("payload too large")
    return HEADER.pack(MAGIC, VERSION, ptype, seq & 0xFFFFFFFF, rate, channels, bits, len(payload)) + payload


def control(ptype: int, text: str = "") -> bytes:
    return pack(ptype, 0, 0, 0, 0, text.encode("utf-8"))


def header_payload_len(header: bytes) -> int:
    """Payload length declared by a 16-byte header, or -1 if it is not a PhoneMic header."""
    if len(header) < HEADER_SIZE or header[:2] != MAGIC or header[2] != VERSION:
        return -1
    return struct.unpack("!H", header[14:16])[0]


def unpack(data: bytes) -> Optional[Packet]:
    """Parse one complete packet; None when the bytes are not a valid packet."""
    if len(data) < HEADER_SIZE:
        return None
    magic, version, ptype, seq, rate, ch, bits, plen = HEADER.unpack_from(data)
    if magic != MAGIC or version != VERSION or len(data) < HEADER_SIZE + plen:
        return None
    return Packet(ptype, seq, rate, ch, bits, bytes(data[HEADER_SIZE:HEADER_SIZE + plen]))


# --------------------------------------------------------------------------- DSP helpers

class Resampler:
    """Streaming linear-interpolation resampler for int16 PCM shaped (frames, channels)."""

    def __init__(self, src_rate: int, dst_rate: int, channels: int):
        self.ratio = src_rate / dst_rate
        self.pos = 0.0
        self.tail = np.zeros((1, channels), dtype=np.float32)
        self.primed = False

    def process(self, pcm: np.ndarray) -> np.ndarray:
        if pcm.size == 0:
            return pcm
        x = pcm.astype(np.float32)
        if self.primed:
            x = np.concatenate([self.tail, x])
        else:
            self.primed = True
        last = len(x) - 1
        if last < 1:
            self.tail = x[-1:]
            return np.zeros((0, pcm.shape[1]), dtype=np.int16)
        n_out = int(np.floor((last - self.pos) / self.ratio)) + 1 if self.pos <= last else 0
        if n_out <= 0:
            self.pos -= last
            self.tail = x[-1:]
            return np.zeros((0, pcm.shape[1]), dtype=np.int16)
        idx = self.pos + np.arange(n_out) * self.ratio
        i0 = np.floor(idx).astype(np.int64)
        i0 = np.minimum(i0, last - 1)
        frac = (idx - i0)[:, None]
        y = x[i0] * (1.0 - frac) + x[i0 + 1] * frac
        self.pos = (idx[-1] + self.ratio) - last
        self.tail = x[-1:]
        return np.clip(np.rint(y), -32768, 32767).astype(np.int16)


def convert_channels(pcm: np.ndarray, out_channels: int) -> np.ndarray:
    """(frames, in_ch) int16 -> (frames, out_ch) int16 by duplicating or averaging."""
    in_ch = pcm.shape[1]
    if in_ch == out_channels:
        return pcm
    if in_ch == 1:
        return np.repeat(pcm, out_channels, axis=1)
    mono = pcm.astype(np.int32).mean(axis=1, keepdims=True).astype(np.int16)
    return np.repeat(mono, out_channels, axis=1) if out_channels > 1 else mono


# --------------------------------------------------------------------------- jitter buffer

@dataclass
class Stats:
    packets: int = 0
    bytes: int = 0
    lost: int = 0
    late: int = 0
    underruns: int = 0
    trims: int = 0
    last_sender: str = ""
    lock: threading.Lock = field(default_factory=threading.Lock)


class JitterBuffer:
    """FIFO of device-rate PCM bytes with pre-buffering, loss concealment and a latency cap.

    push() is called by network threads, pull() by the audio callback.
    """

    SLACK_MS = 30   # tolerated steady-state excess above target before tightening
    HOLD_S = 3.0    # how long the excess must persist

    def __init__(self, stats: Stats, target_ms: int, max_ms: int):
        self.stats = stats
        self.target_ms = target_ms
        self.max_ms = max_ms
        self.lock = threading.Lock()
        self.buf = bytearray()
        self.filling = True
        self.expected_seq: Optional[int] = None
        self.rate = 48000
        self.channels = 1
        self.frame_bytes = 2
        self.high_since: Optional[float] = None

    def configure(self, rate: int, channels: int) -> None:
        with self.lock:
            self.rate = rate
            self.channels = channels
            self.frame_bytes = 2 * channels
            self.buf.clear()
            self.filling = True
            self.expected_seq = None
            self.high_since = None

    def _ms_to_bytes(self, ms: int) -> int:
        n = int(self.rate * ms / 1000) * self.frame_bytes
        return max(n, self.frame_bytes)

    def buffered_ms(self) -> float:
        with self.lock:
            return len(self.buf) / self.frame_bytes / self.rate * 1000.0

    def reset_sequence(self) -> None:
        with self.lock:
            self.expected_seq = None

    def push(self, seq: int, pcm: bytes) -> None:
        with self.lock:
            if self.expected_seq is not None:
                gap = (seq - self.expected_seq) & 0xFFFFFFFF
                if gap == 0:
                    pass
                elif gap <= 50:
                    # Lost packets: conceal with silence so timing stays intact.
                    self.stats.lost += gap
                    self.buf.extend(bytes(gap * len(pcm)))
                elif gap >= 0xFFFFFFFF - 50:
                    # Late or duplicate packet: already concealed, drop it.
                    self.stats.late += 1
                    return
                else:
                    # Sender restarted or huge jump: resynchronise.
                    self.buf.clear()
                    self.filling = True
            self.expected_seq = (seq + 1) & 0xFFFFFFFF
            self.buf.extend(pcm)
            max_bytes = self._ms_to_bytes(self.max_ms)
            if len(self.buf) > max_bytes:
                # We are falling behind (e.g. audio device stalled); drop the oldest audio.
                excess = len(self.buf) - self._ms_to_bytes(self.target_ms)
                excess -= excess % self.frame_bytes
                del self.buf[:excess]
                self.stats.trims += 1

    def _tighten_latency(self) -> None:
        """Bursts and clock drift make the buffer settle above target; after it has stayed more than
        SLACK_MS above target for HOLD_S seconds, drop the excess once (a barely audible skip)."""
        excess = len(self.buf) - self._ms_to_bytes(self.target_ms + self.SLACK_MS)
        if excess <= 0:
            self.high_since = None
            return
        now = time.monotonic()
        if self.high_since is None:
            self.high_since = now
        elif now - self.high_since >= self.HOLD_S:
            drop = len(self.buf) - self._ms_to_bytes(self.target_ms)
            drop -= drop % self.frame_bytes
            del self.buf[:drop]
            self.stats.trims += 1
            self.high_since = None

    def pull(self, nbytes: int) -> bytes:
        with self.lock:
            if self.filling:
                if len(self.buf) >= self._ms_to_bytes(self.target_ms):
                    self.filling = False
                else:
                    return bytes(nbytes)
            if len(self.buf) >= nbytes:
                out = bytes(self.buf[:nbytes])
                del self.buf[:nbytes]
                self._tighten_latency()
                return out
            # Underrun: play what we have, pad with silence, then re-buffer.
            out = bytes(self.buf) + bytes(nbytes - len(self.buf))
            self.buf.clear()
            self.filling = True
            self.stats.underruns += 1
            return out


# --------------------------------------------------------------------------- audio output

class Player:
    """Owns the sounddevice OutputStream and adapts the phone's format to the device's."""

    def __init__(self, device, jitter: JitterBuffer, verbose: bool, enabled: bool):
        self.device = device
        self.jitter = jitter
        self.verbose = verbose
        self.enabled = enabled
        self.lock = threading.Lock()
        self.stream = None
        self.in_rate = 0
        self.in_channels = 0
        self.out_rate = 0
        self.out_channels = 0
        self.resampler: Optional[Resampler] = None

    def ensure_format(self, rate: int, channels: int) -> None:
        if rate == self.in_rate and channels == self.in_channels:
            return
        with self.lock:
            if rate == self.in_rate and channels == self.in_channels:
                return
            self._close()
            self.in_rate, self.in_channels = rate, channels
            self.out_rate, self.out_channels = rate, channels
            if self.enabled:
                self._open()
            self.jitter.configure(self.out_rate, self.out_channels)
            self.resampler = Resampler(rate, self.out_rate, channels) if self.out_rate != rate else None
            print(f"[audio] phone sends {rate} Hz x{channels}; device runs {self.out_rate} Hz x{self.out_channels}"
                  + ("" if self.enabled else " (playback disabled)"), flush=True)

    def _open(self) -> None:
        import sounddevice as sd

        info = sd.query_devices(self.device, "output")
        max_ch = int(info["max_output_channels"])
        default_rate = int(round(info["default_samplerate"]))
        want_ch = self.in_channels if self.in_channels <= max_ch else max_ch
        # Prefer the phone's own sample rate; otherwise fall back to the device default and resample.
        for rate, ch in ((self.in_rate, want_ch), (default_rate, want_ch), (default_rate, max_ch)):
            try:
                sd.check_output_settings(device=self.device, samplerate=rate, channels=ch, dtype="int16")
            except Exception:
                continue
            self.out_rate, self.out_channels = rate, ch
            break
        else:
            self.out_rate, self.out_channels = default_rate, max_ch
        self.stream = sd.OutputStream(
            device=self.device,
            samplerate=self.out_rate,
            channels=self.out_channels,
            dtype="int16",
            latency="low",
            callback=self._callback,
        )
        self.stream.start()

    def _callback(self, outdata, frames, time_info, status):  # noqa: D401 (sounddevice signature)
        if status and self.verbose:
            print(f"[audio] {status}", flush=True)
        data = self.jitter.pull(frames * self.out_channels * 2)
        outdata[:] = np.frombuffer(data, dtype=np.int16).reshape(frames, self.out_channels)

    def feed(self, pcm_bytes: bytes, seq: int) -> None:
        """Convert one phone packet to the device format and queue it."""
        if not self.enabled:
            return  # nothing pulls from the buffer without a device; recording happens elsewhere
        if self.resampler is None and self.out_channels == self.in_channels:
            self.jitter.push(seq, pcm_bytes)
            return
        pcm = np.frombuffer(pcm_bytes, dtype=np.int16)
        pcm = pcm[: len(pcm) - len(pcm) % self.in_channels].reshape(-1, self.in_channels)
        if self.resampler is not None:
            pcm = self.resampler.process(pcm)
        pcm = convert_channels(pcm, self.out_channels)
        self.jitter.push(seq, pcm.tobytes())

    def _close(self) -> None:
        if self.stream is not None:
            try:
                self.stream.stop()
                self.stream.close()
            except Exception:
                pass
            self.stream = None

    def close(self) -> None:
        with self.lock:
            self._close()


class WavRecorder:
    """Writes incoming PCM to a WAV file; a format change mid-session rolls over to name-2.wav etc."""

    def __init__(self, path: str):
        self.path = path
        self.lock = threading.Lock()
        self.wav: Optional[wave.Wave_write] = None
        self.rate = 0
        self.channels = 0
        self.take = 1

    def write(self, pcm: bytes, rate: int, channels: int) -> None:
        with self.lock:
            if self.wav is None or rate != self.rate or channels != self.channels:
                path = self.path
                if self.wav is not None:
                    self.wav.close()
                    self.take += 1
                    stem, ext = os.path.splitext(self.path)
                    path = f"{stem}-{self.take}{ext or '.wav'}"
                    print(f"[rec] stream format changed; continuing in {path}", flush=True)
                else:
                    print(f"[rec] recording to {path}", flush=True)
                self.rate, self.channels = rate, channels
                self.wav = wave.open(path, "wb")
                self.wav.setnchannels(channels)
                self.wav.setsampwidth(2)
                self.wav.setframerate(rate)
            self.wav.writeframes(pcm)

    def close(self) -> None:
        with self.lock:
            if self.wav is not None:
                self.wav.close()
                self.wav = None


# --------------------------------------------------------------------------- receiver

class Receiver:
    def __init__(self, args):
        self.args = args
        self.stats = Stats()
        self.jitter = JitterBuffer(self.stats, args.buffer_ms, args.max_buffer_ms)
        self.player = Player(args.device_index, self.jitter, args.verbose, enabled=not args.no_playback)
        self.recorder = WavRecorder(args.record) if args.record else None
        self.name = args.name or socket.gethostname()
        self.stop = threading.Event()
        self.threads: list[threading.Thread] = []

    # -- packet handling shared by UDP and TCP ---------------------------------------------
    def handle(self, pkt: Packet, sender: str, reply) -> None:
        if pkt.type == TYPE_AUDIO:
            if pkt.bits != 16 or pkt.channels not in (1, 2) or not (8000 <= pkt.sample_rate <= 192000):
                return
            if sender != self.stats.last_sender:
                print(f"[net] audio from {sender}", flush=True)
                self.stats.last_sender = sender
                self.jitter.reset_sequence()
            self.player.ensure_format(pkt.sample_rate, pkt.channels)
            self.player.feed(pkt.payload, pkt.seq)
            if self.recorder is not None:
                self.recorder.write(pkt.payload, pkt.sample_rate, pkt.channels)
            with self.stats.lock:
                self.stats.packets += 1
                self.stats.bytes += len(pkt.payload)
        elif pkt.type == TYPE_PING:
            reply(control(TYPE_PONG, self.name))
        elif pkt.type == TYPE_DISCOVER:
            print(f"[net] discovery request from {sender} ({pkt.text or 'phone'})", flush=True)
            reply(control(TYPE_ANNOUNCE, self.name))
        elif pkt.type == TYPE_BYE:
            print(f"[net] {sender} stopped streaming", flush=True)

    # -- UDP --------------------------------------------------------------------------------
    def udp_loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
        except OSError:
            pass
        sock.bind(("0.0.0.0", self.args.port))
        sock.settimeout(0.5)
        while not self.stop.is_set():
            try:
                data, addr = sock.recvfrom(MAX_PACKET)
            except socket.timeout:
                continue
            except OSError:
                break
            pkt = unpack(data)
            if pkt is None:
                continue
            self.handle(pkt, f"{addr[0]} (UDP)", lambda b, a=addr: sock.sendto(b, a))
        sock.close()

    # -- TCP --------------------------------------------------------------------------------
    def tcp_accept_loop(self) -> None:
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", self.args.port))
        srv.listen(4)
        srv.settimeout(0.5)
        while not self.stop.is_set():
            try:
                conn, addr = srv.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            t = threading.Thread(target=self.tcp_client_loop, args=(conn, addr), daemon=True)
            t.start()
        srv.close()

    def tcp_client_loop(self, conn: socket.socket, addr) -> None:
        label = f"{addr[0]} (TCP)"
        print(f"[net] TCP connection from {label}", flush=True)
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        conn.settimeout(10.0)
        send_lock = threading.Lock()

        def reply(b: bytes) -> None:
            with send_lock:
                try:
                    conn.sendall(b)
                except OSError:
                    pass

        def read_exact(n: int) -> Optional[bytes]:
            chunks = bytearray()
            while len(chunks) < n:
                try:
                    part = conn.recv(n - len(chunks))
                except socket.timeout:
                    if self.stop.is_set():
                        return None
                    continue
                except OSError:
                    return None
                if not part:
                    return None
                chunks.extend(part)
            return bytes(chunks)

        try:
            while not self.stop.is_set():
                header = read_exact(HEADER_SIZE)
                if header is None:
                    break
                plen = header_payload_len(header)
                if plen < 0:
                    print(f"[net] {label}: bad header, closing", flush=True)
                    break
                payload = read_exact(plen) if plen else b""
                if payload is None:
                    break
                pkt = unpack(header + payload)
                if pkt is not None:
                    self.handle(pkt, label, reply)
        finally:
            conn.close()
            print(f"[net] {label} disconnected", flush=True)

    # -- stats ------------------------------------------------------------------------------
    def stats_loop(self) -> None:
        last_packets = 0
        last_bytes = 0
        while not self.stop.wait(2.0):
            with self.stats.lock:
                p, b = self.stats.packets, self.stats.bytes
            dp, db = p - last_packets, b - last_bytes
            last_packets, last_bytes = p, b
            if dp == 0 and not self.args.verbose:
                if p == 0:
                    print("[stats] waiting for the phone...", flush=True)
                continue
            kbps = db * 8 / 2000.0
            print(
                f"[stats] {self.stats.last_sender or '-'} | {dp / 2.0:.0f} pkt/s | {kbps:.0f} kbit/s | "
                f"buffer {self.jitter.buffered_ms():.0f} ms | lost {self.stats.lost} | late {self.stats.late} | "
                f"underruns {self.stats.underruns} | trims {self.stats.trims}",
                flush=True,
            )

    # -- lifecycle --------------------------------------------------------------------------
    def run(self) -> None:
        for target in (self.udp_loop, self.tcp_accept_loop, self.stats_loop):
            t = threading.Thread(target=target, daemon=True, name=target.__name__)
            t.start()
            self.threads.append(t)
        print(f"[net] listening on UDP+TCP port {self.args.port} as '{self.name}'. Press Ctrl+C to quit.", flush=True)
        try:
            while not self.stop.is_set():
                time.sleep(0.5)
        except KeyboardInterrupt:
            pass
        finally:
            self.shutdown()

    def shutdown(self) -> None:
        self.stop.set()
        for t in self.threads:
            t.join(timeout=2.0)
        self.player.close()
        if self.recorder is not None:
            self.recorder.close()
        print("[net] stopped.", flush=True)


# --------------------------------------------------------------------------- device selection

def list_devices() -> None:
    import sounddevice as sd

    print(sd.query_devices())
    print("\nOutput devices you can pass to --device (index or part of the name):")
    for i, d in enumerate(sd.query_devices()):
        if d["max_output_channels"] > 0:
            tag = "  <-- virtual cable" if "cable input" in d["name"].lower() else ""
            print(f"  {i:3d}  {d['name']}  [{d['hostapi']}]{tag}")


def resolve_device(spec: Optional[str]) -> Optional[int]:
    import sounddevice as sd

    devices = sd.query_devices()
    outputs = [(i, d) for i, d in enumerate(devices) if d["max_output_channels"] > 0]
    if spec is None:
        for i, d in outputs:
            if "cable input" in d["name"].lower():
                print(f"[audio] using virtual cable: [{i}] {d['name']}", flush=True)
                return i
        default = sd.default.device[1]
        name = devices[default]["name"] if default is not None and default >= 0 else "default output"
        print(
            "[audio] WARNING: no VB-CABLE found; playing through the default output "
            f"('{name}'). Install VB-CABLE (https://vb-audio.com/Cable/) so apps can use the phone as a mic.",
            flush=True,
        )
        return None
    if spec.isdigit():
        idx = int(spec)
        if idx < 0 or idx >= len(devices) or devices[idx]["max_output_channels"] <= 0:
            sys.exit(f"device index {idx} is not an output device (see --list-devices)")
        return idx
    matches = [(i, d) for i, d in outputs if spec.lower() in d["name"].lower()]
    if not matches:
        sys.exit(f"no output device matches '{spec}' (see --list-devices)")
    # Prefer the Windows MME/WASAPI entry with the most channels when several host APIs match.
    idx, d = sorted(matches, key=lambda x: (-x[1]["max_output_channels"], x[0]))[0]
    print(f"[audio] using [{idx}] {d['name']}", flush=True)
    return idx


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Receive the PhoneMic Android app's microphone stream.")
    p.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"UDP+TCP port (default {DEFAULT_PORT})")
    p.add_argument("--device", help="output device index or name fragment (default: 'CABLE Input' if present)")
    p.add_argument("--list-devices", action="store_true", help="list audio devices and exit")
    p.add_argument("--buffer-ms", type=int, default=40, help="jitter buffer target in ms (default 40; raise if audio breaks up)")
    p.add_argument("--max-buffer-ms", type=int, default=250, help="latency cap in ms; older audio is dropped (default 250)")
    p.add_argument("--record", metavar="FILE.wav", help="also write the received audio to a WAV file")
    p.add_argument("--no-playback", action="store_true", help="do not open an audio device (use with --record or for testing)")
    p.add_argument("--name", help="name announced to phones during discovery (default: this computer's host name)")
    p.add_argument("--verbose", "-v", action="store_true")
    args = p.parse_args(argv)
    if args.buffer_ms < 5 or args.max_buffer_ms <= args.buffer_ms:
        p.error("--buffer-ms must be >= 5 and smaller than --max-buffer-ms")
    return args


def main(argv=None) -> None:
    args = parse_args(argv)
    if args.list_devices:
        list_devices()
        return
    args.device_index = None if args.no_playback else resolve_device(args.device)
    Receiver(args).run()


if __name__ == "__main__":
    main()
