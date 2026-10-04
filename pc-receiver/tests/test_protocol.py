"""Unit tests for the PC receiver. Run with:  python -m unittest discover -s tests -v"""
import os
import sys
import time
import unittest

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from phonemic_receiver import (  # noqa: E402
    HEADER_SIZE, TYPE_ANNOUNCE, TYPE_AUDIO, TYPE_PING, JitterBuffer, Resampler, Stats, control,
    convert_channels, header_payload_len, pack, unpack,
)


class ProtocolTest(unittest.TestCase):
    def test_audio_header_matches_shared_vector(self):
        # Same vector as the Kotlin PmicTest - keeps the app and the receiver in lock-step.
        pcm = bytes([0x01, 0x00, 0xFF, 0x7F])
        out = pack(TYPE_AUDIO, 258, 48000, 1, 16, pcm)
        self.assertEqual(out.hex(), "504d0101" "00000102" "0000bb80" "01" "10" "0004" "0100ff7f")

    def test_round_trip(self):
        pcm = bytes(range(256)) * 3
        raw = pack(TYPE_AUDIO, 0xFFFFFFFF, 44100, 2, 16, pcm)
        pkt = unpack(raw)
        self.assertEqual((pkt.type, pkt.seq, pkt.sample_rate, pkt.channels, pkt.bits), (TYPE_AUDIO, 0xFFFFFFFF, 44100, 2, 16))
        self.assertEqual(pkt.payload, pcm)
        self.assertEqual(header_payload_len(raw[:HEADER_SIZE]), len(pcm))

    def test_control_text(self):
        raw = control(TYPE_ANNOUNCE, "Gaming-PC ✓")
        pkt = unpack(raw)
        self.assertEqual(pkt.type, TYPE_ANNOUNCE)
        self.assertEqual(pkt.seq, 0)
        self.assertEqual(pkt.text, "Gaming-PC ✓")

    def test_rejects_garbage(self):
        self.assertIsNone(unpack(b"abc"))
        bad = bytearray(control(TYPE_PING))
        bad[0] = ord("X")
        self.assertIsNone(unpack(bytes(bad)))
        self.assertEqual(header_payload_len(bytes(bad)), -1)
        truncated = control(TYPE_ANNOUNCE, "hello")
        self.assertIsNone(unpack(truncated[:-2]))


class JitterBufferTest(unittest.TestCase):
    def make(self, target_ms=40, max_ms=250, rate=48000, channels=1):
        jb = JitterBuffer(Stats(), target_ms, max_ms)
        jb.configure(rate, channels)
        return jb

    def frame(self, value, rate=48000, ms=10, channels=1):
        return np.full(rate * ms // 1000 * channels, value, dtype=np.int16).tobytes()

    def test_prebuffers_then_plays(self):
        jb = self.make(target_ms=40)
        jb.push(0, self.frame(1))
        self.assertEqual(jb.pull(960), bytes(960))  # still filling: silence, nothing consumed
        for seq in range(1, 4):
            jb.push(seq, self.frame(seq + 1))
        out = np.frombuffer(jb.pull(960), dtype=np.int16)
        self.assertTrue(np.all(out == 1))
        self.assertAlmostEqual(jb.buffered_ms(), 30.0, places=3)

    def test_loss_is_concealed_with_silence(self):
        jb = self.make(target_ms=10)
        jb.push(0, self.frame(5))
        jb.push(2, self.frame(7))  # seq 1 lost
        self.assertEqual(jb.stats.lost, 1)
        jb.pull(960)  # frame 0
        gap = np.frombuffer(jb.pull(960), dtype=np.int16)
        self.assertTrue(np.all(gap == 0))
        nxt = np.frombuffer(jb.pull(960), dtype=np.int16)
        self.assertTrue(np.all(nxt == 7))

    def test_late_packet_dropped(self):
        jb = self.make(target_ms=10)
        jb.push(5, self.frame(1))
        jb.push(6, self.frame(2))
        jb.push(4, self.frame(9))  # late
        self.assertEqual(jb.stats.late, 1)
        self.assertAlmostEqual(jb.buffered_ms(), 20.0, places=3)

    def test_latency_cap_trims_old_audio(self):
        jb = self.make(target_ms=40, max_ms=100)
        for seq in range(15):  # 150 ms without any pull
            jb.push(seq, self.frame(seq))
        self.assertEqual(jb.stats.trims, 1)
        self.assertLessEqual(jb.buffered_ms(), 100.0)
        first = np.frombuffer(jb.pull(960), dtype=np.int16)
        self.assertGreater(first[0], 0)  # oldest frames were dropped, newest kept

    def test_underrun_refills(self):
        jb = self.make(target_ms=10)
        jb.push(0, self.frame(3))
        jb.pull(960)
        out = jb.pull(960)
        self.assertEqual(out, bytes(960))
        self.assertEqual(jb.stats.underruns, 1)
        self.assertTrue(jb.filling)


class LatencyTighteningTest(unittest.TestCase):
    def test_persistent_excess_is_trimmed_once(self):
        import phonemic_receiver as pr
        jb = JitterBuffer(Stats(), 40, 500)
        jb.configure(48000, 1)
        jb.HOLD_S = 0.05
        frame = np.ones(480, dtype=np.int16).tobytes()  # 10 ms
        for seq in range(20):  # 200 ms queued, far above 40 ms target
            jb.push(seq, frame)
        jb.pull(960)  # leaves filling state, starts the excess timer
        self.assertEqual(jb.stats.trims, 0)
        time.sleep(0.06)
        jb.pull(960)
        self.assertEqual(jb.stats.trims, 1)
        self.assertLessEqual(jb.buffered_ms(), 40.0 + 0.01)
        jb.pull(960)
        self.assertEqual(jb.stats.trims, 1)  # not repeated once back at target


class DspTest(unittest.TestCase):
    def test_resampler_ratio_and_continuity(self):
        rs = Resampler(48000, 44100, 1)
        total_out = 0
        t = np.arange(48000) / 48000.0
        sig = (10000 * np.sin(2 * np.pi * 440 * t)).astype(np.int16)[:, None]
        outs = []
        for i in range(0, 48000, 480):
            out = rs.process(sig[i:i + 480])
            total_out += len(out)
            outs.append(out)
        self.assertTrue(abs(total_out - 44100) <= 2, total_out)
        y = np.concatenate(outs)[:, 0].astype(np.float64)
        # Continuity: no jumps larger than what a 440 Hz sine can produce between samples.
        max_step = np.max(np.abs(np.diff(y)))
        self.assertLess(max_step, 10000 * 2 * np.pi * 440 / 44100 * 1.5)

    def test_resampler_identity_like_when_rates_match(self):
        rs = Resampler(48000, 48000, 2)
        x = np.arange(20, dtype=np.int16).reshape(10, 2)
        y = rs.process(x)
        self.assertEqual(len(y), 10)
        np.testing.assert_array_equal(y, x)

    def test_channel_conversion(self):
        mono = np.array([[1], [2], [3]], dtype=np.int16)
        np.testing.assert_array_equal(convert_channels(mono, 2), [[1, 1], [2, 2], [3, 3]])
        stereo = np.array([[2, 4], [-2, 2]], dtype=np.int16)
        np.testing.assert_array_equal(convert_channels(stereo, 1), [[3], [0]])
        np.testing.assert_array_equal(convert_channels(stereo, 2), stereo)


if __name__ == "__main__":
    unittest.main()
