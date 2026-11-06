"""Lightweight Pi -> AeroHelm cloud observation-only bridge (stdlib only).

Consumes fresh, normalized JSON lines from the EXISTING UART-owning agent
over stdin. Never opens a serial port, sends MAVLink, or retries an uncertain
old sample. Networking is isolated on a worker thread with a bounded queue.
"""
from __future__ import annotations

import argparse
import json
import logging
import math
import os
import queue
import sys
import threading
import time
from urllib import error, request

from .identity import DeviceTokenProvider, secure_opener, validated_https_url

LOG = logging.getLogger("aerohelm.pi.bridge")
MAX_BYTES = 2048
MAX_AGE_MS = 2500
ALLOWED = frozenset((
    "captured_at_ms", "latitude", "longitude", "altitude_m",
    "battery_pct", "armed", "mode", "gps_fix_type", "link_rtt_ms",
))
MEASUREMENTS = {
    "latitude": (-90, 90), "longitude": (-180, 180),
    "altitude_m": (-1000, 30000), "battery_pct": (0, 100),
    "link_rtt_ms": (0, 60000),
}


def normalize(raw: object, *, now_ms: int) -> dict | None:
    if not isinstance(raw, dict) or set(raw) - ALLOWED:
        return None
    captured = raw.get("captured_at_ms")
    if (type(captured) is not int or captured > now_ms + 500 or
            now_ms - captured > MAX_AGE_MS):
        return None
    if type(raw.get("armed")) is not bool:
        return None
    mode = raw.get("mode")
    if (not isinstance(mode, str) or not 1 <= len(mode) <= 32 or
            any(not (ch.isalnum() or ch in "_-") for ch in mode)):
        return None
    values = {"armed": raw["armed"], "mode": mode}
    for key, (lo, hi) in MEASUREMENTS.items():
        if key in raw and raw[key] is not None:
            value = raw[key]
            if (type(value) not in (float, int) or
                    not math.isfinite(value) or not lo <= value <= hi):
                return None
            values[key] = float(value)
    if "gps_fix_type" in raw and raw["gps_fix_type"] is not None:
        value = raw["gps_fix_type"]
        if type(value) is not int or not 0 <= value <= 6:
            return None
        values["gps_fix_type"] = value
    return values


class PrivateCloud:
    def __init__(self, *, origin: str, token_provider: DeviceTokenProvider,
                 cafile: str | None = None, opener=None):
        self.origin = validated_https_url(origin, permit_path=False).rstrip("/")
        self.provider = token_provider
        self.opener = opener or secure_opener(cafile)

    def post(self, path: str, data: dict) -> dict:
        if not path.startswith("/api/v1/device/telemetry"):
            raise ValueError("Pi observation bridge cannot send control requests")
        body = json.dumps(data, allow_nan=False, separators=(",", ":")).encode()
        if len(body) > MAX_BYTES:
            raise ValueError("Bounded telemetry request exceeded 2048 bytes")
        outgoing = request.Request(
            self.origin + path, data=body, method="POST",
            headers={"Content-Type": "application/json",
                     "Authorization": "Bearer " + self.provider.get()},
        )
        try:
            with self.opener.open(outgoing, timeout=3) as response:
                if response.status not in (200, 202):
                    raise ValueError("Unexpected cloud telemetry status")
                raw = response.read(2049)
        except error.HTTPError as exc:
            if exc.code == 401:
                self.provider.invalidate()
            raise
        if len(raw) > 2048:
            raise ValueError("Cloud response exceeds size budget")
        data = json.loads(raw)
        if not isinstance(data, dict):
            raise ValueError("Cloud did not return a JSON object")
        return data

    def session(self) -> int:
        answer = self.post("/api/v1/device/telemetry/session", {})
        epoch = answer.get("session_epoch")
        if type(epoch) is not int or not 1 <= epoch <= 2147483647:
            raise ValueError("Cloud did not issue a current session epoch")
        return epoch

    def sample(self, sample: dict, epoch: int, sequence: int) -> None:
        result = self.post("/api/v1/device/telemetry", {
            **sample, "epoch": epoch, "sequence": sequence,
        })
        if (result.get("accepted") is not True or
                result.get("sequence") != sequence or
                result.get("session_epoch") != epoch):
            raise ValueError("Cloud receipt does not match observation")


class ObservationBridge:
    def __init__(self, cloud: PrivateCloud, *, clock=time.time):
        self.cloud = cloud
        self.clock = clock
        self.samples: queue.Queue = queue.Queue(maxsize=16)
        self.accepted = 0
        self.dropped = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._sender, daemon=True)

    def enqueue(self, raw: object) -> bool:
        now_ms = int(self.clock() * 1000)
        normalized = normalize(raw, now_ms=now_ms)
        if normalized is None:
            self.dropped += 1
            return False
        entry = (now_ms, normalized)
        if self.samples.full():
            try:
                self.samples.get_nowait()  # Drop OLDEST, never backpressure FC.
                self.dropped += 1
            except queue.Empty:
                pass
        try:
            self.samples.put_nowait(entry)
            return True
        except queue.Full:
            self.dropped += 1
            return False

    def start(self):
        self._thread.start()

    def stop(self):
        self._stop.set()
        self._thread.join(timeout=3)

    def _sender(self):
        epoch = None
        seq = 0
        backoff = 1.0
        while not self._stop.is_set():
            try:
                captured, sample = self.samples.get(timeout=0.25)
            except queue.Empty:
                continue
            if int(self.clock() * 1000) - captured > MAX_AGE_MS:
                self.dropped += 1
                continue
            try:
                if epoch is None:
                    epoch = self.cloud.session()
                    seq = 0
                seq += 1
                self.cloud.sample(sample, epoch, seq)
                self.accepted += 1
                backoff = 1.0
            except Exception as exc:
                # Never replay ambiguous requests or use previous epoch.
                epoch = None
                seq = 0
                self.dropped += 1
                LOG.warning("Pi/cloud observation path unavailable (%s)",
                            type(exc).__name__)
                self._stop.wait(backoff)
                backoff = min(backoff * 2, 20.0)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--token-endpoint", required=True)
    parser.add_argument("--client-id", required=True)
    parser.add_argument("--secret-file", required=True)
    parser.add_argument("--cafile")
    args = parser.parse_args()
    provider = DeviceTokenProvider(
        token_endpoint=args.token_endpoint, client_id=args.client_id,
        client_secret_file=args.secret_file, cafile=args.cafile,
    )
    bridge = ObservationBridge(PrivateCloud(
        origin=args.origin, token_provider=provider, cafile=args.cafile))
    logging.basicConfig(level=logging.INFO)
    bridge.start()
    try:
        while True:
            # Bounded ingestion; when a producer exceeds the limit, drain
            # remainder of that JSON line and record a drop.
            line = sys.stdin.buffer.readline(MAX_BYTES + 1)
            if not line:
                break
            if len(line) > MAX_BYTES:
                while line and not line.endswith(b"\n"):
                    line = sys.stdin.buffer.readline(MAX_BYTES + 1)
                bridge.dropped += 1
                continue
            try:
                sample = json.loads(line)
            except (ValueError, UnicodeDecodeError):
                bridge.dropped += 1
                continue
            bridge.enqueue(sample)
    finally:
        bridge.stop()
    LOG.info("Pi observation bridge stopped: accepted=%d dropped=%d",
             bridge.accepted, bridge.dropped)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
