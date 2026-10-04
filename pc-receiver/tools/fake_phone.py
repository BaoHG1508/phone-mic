#!/usr/bin/env python3
"""Pretends to be the PhoneMic Android app: streams a test tone (or a WAV file) to the receiver.

Use it to check the PC side without a phone, e.g. that VB-CABLE routing works:

    python tools/fake_phone.py --host 127.0.0.1 --seconds 5
    python tools/fake_phone.py --tcp --wav voice.wav
    python tools/fake_phone.py --discover          # who answers on this LAN?
"""
from __future__ import annotations

import argparse
import os
import socket
import sys
import time
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from phonemic_receiver import (  # noqa: E402
    DEFAULT_PORT, HEADER_SIZE, MAX_PACKET, TYPE_ANNOUNCE, TYPE_AUDIO, TYPE_BYE, TYPE_DISCOVER, TYPE_PING,
    TYPE_PONG, control, header_payload_len, pack, unpack,
)


def frame_ms_for(rate: int, channels: int) -> int:
    bytes_per_ms = rate * channels * 2 / 1000.0
    for ms in (20, 10, 5):
        if ms * bytes_per_ms <= 1200:
            return ms
    return 5


def tone_frames(rate: int, channels: int, freq: float, seconds: float, amplitude: float = 0.3):
    n = int(rate * seconds)
    t = np.arange(n) / rate
    mono = (amplitude * 32767 * np.sin(2 * np.pi * freq * t)).astype(np.int16)
    if channels == 2:
        right = (amplitude * 32767 * np.sin(2 * np.pi * freq * 1.5 * t)).astype(np.int16)
        return np.stack([mono, right], axis=1)
    return mono[:, None]


def wav_frames(path: str):
    with wave.open(path, "rb") as w:
        if w.getsampwidth() != 2:
            sys.exit("only 16-bit WAV files are supported")
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
        return data.reshape(-1, w.getnchannels()), w.getframerate()


def discover(port: int, timeout: float) -> None:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    sock.settimeout(0.3)
    probe = control(TYPE_DISCOVER, "fake_phone")
    deadline = time.time() + timeout
    seen = {}
    next_probe = 0.0
    while time.time() < deadline:
        if time.time() >= next_probe:
            for target in ("255.255.255.255", "127.0.0.1"):
                try:
                    sock.sendto(probe, (target, port))
                except OSError:
                    pass
            next_probe = time.time() + 0.7
        try:
            data, addr = sock.recvfrom(MAX_PACKET)
        except socket.timeout:
            continue
        pkt = unpack(data)
        if pkt and pkt.type == TYPE_ANNOUNCE and addr[0] not in seen:
            seen[addr[0]] = pkt.text
            print(f"found PC receiver: {pkt.text} at {addr[0]}")
    if not seen:
        print("no receiver answered")


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=DEFAULT_PORT)
    p.add_argument("--tcp", action="store_true", help="use TCP instead of UDP")
    p.add_argument("--rate", type=int, default=48000)
    p.add_argument("--channels", type=int, default=1, choices=(1, 2))
    p.add_argument("--freq", type=float, default=440.0)
    p.add_argument("--amplitude", type=float, default=0.3, help="tone level 0..1 (default 0.3)")
    p.add_argument("--seconds", type=float, default=5.0)
    p.add_argument("--wav", help="stream this 16-bit WAV file instead of a tone")
    p.add_argument("--drop-every", type=int, default=0, help="simulate loss: skip every Nth packet (UDP)")
    p.add_argument("--discover", action="store_true", help="only run discovery and exit")
    args = p.parse_args()

    if args.discover:
        discover(args.port, 3.0)
        return

    if args.wav:
        frames, rate = wav_frames(args.wav)
        channels = frames.shape[1]
    else:
        rate, channels = args.rate, args.channels
        frames = tone_frames(rate, channels, args.freq, args.seconds, args.amplitude)

    frame_ms = frame_ms_for(rate, channels)
    spf = rate * frame_ms // 1000  # samples per frame
    total = len(frames) // spf
    print(f"streaming {total} packets of {frame_ms} ms ({rate} Hz x{channels}) to {args.host}:{args.port} via "
          f"{'TCP' if args.tcp else 'UDP'}")

    if args.tcp:
        sock = socket.create_connection((args.host, args.port), timeout=3)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        sock.settimeout(0.0)
        send = sock.sendall
    else:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(0.0)
        send = lambda b: sock.sendto(b, (args.host, args.port))  # noqa: E731

    pongs = 0
    tcp_rx = bytearray()

    def poll_replies() -> None:
        nonlocal pongs, tcp_rx
        try:
            data = sock.recv(4096)
        except (BlockingIOError, socket.timeout):
            return
        except OSError:
            return
        if args.tcp:
            tcp_rx.extend(data)
            while len(tcp_rx) >= HEADER_SIZE:
                plen = header_payload_len(bytes(tcp_rx[:HEADER_SIZE]))
                if plen < 0:
                    tcp_rx.clear()
                    break
                if len(tcp_rx) < HEADER_SIZE + plen:
                    break
                pkt = unpack(bytes(tcp_rx[:HEADER_SIZE + plen]))
                del tcp_rx[:HEADER_SIZE + plen]
                if pkt and pkt.type == TYPE_PONG:
                    pongs += 1
        else:
            pkt = unpack(data)
            if pkt and pkt.type == TYPE_PONG:
                pongs += 1

    start = time.perf_counter()
    next_ping = start + 1.0
    sent = 0
    for i in range(total):
        chunk = frames[i * spf:(i + 1) * spf]
        packet = pack(TYPE_AUDIO, i, rate, channels, 16, chunk.tobytes())
        if not (args.drop_every and (i + 1) % args.drop_every == 0):
            send(packet)
            sent += 1
        now = time.perf_counter()
        if now >= next_ping:
            send(control(TYPE_PING, "fake_phone"))
            next_ping += 1.0
        poll_replies()
        target = start + (i + 1) * frame_ms / 1000.0
        delay = target - time.perf_counter()
        if delay > 0:
            time.sleep(delay)
    send(control(TYPE_BYE))
    time.sleep(0.2)
    poll_replies()
    sock.close()
    print(f"done: sent {sent} audio packets, got {pongs} PONG replies")


if __name__ == "__main__":
    main()
