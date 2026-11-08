"""Coalesced send-to-user (PC gates) — behavior tests with in-process HTTP stubs.

No live server needed: stubs run on loopback ports, same pattern as test_ua1.
Each gate maps to the handover acceptance list (§6):

  PC-1 offer() never blocks the caller >1 ms (validate + enqueue only)
  PC-2 single-flight: never more than one in-flight publish per handle
  PC-3 observed event order matches offer order (batches + chunks)
  PC-4 drain(): flush remainder, stats, propagate last error, closed after
  PC-5 429: honor retry_after, retry the same batch once, then drop + record
  PC-6 max_pending_chars bounds memory (drop-oldest, newest reasoning wins)
  PC-7 wire shape: batch envelope, chunks verbatim, one request per flush
"""

from __future__ import annotations

import http.server
import json
import socket
import threading
import time

import pytest

from ar_realtime_client import (
    RealtimeApiError,
    RealtimePublisher,
    RealtimePublisherOptions,
    RealtimeValidationError,
    StaticTokenProvider,
)


# ── stub server plumbing ─────────────────────────────────────────────────────

class StubState:
    """What the stub 'server' observed: ordered payloads + concurrency bookkeeping.

    responses: **scripted status codes** consumed in order, then 200 forever.
    hang_first: optionally block the FIRST request until an Event fires
    (models a suspended HTTP round-trip so offers pile up client-side).
    """

    def __init__(self, delay: float = 0.0, single_flight_probe: bool = False):
        self.lock = threading.Lock()
        self.envelopes: list[dict] = []   # raw request payloads, per request
        self.delay = delay
        self.single_flight_probe = single_flight_probe
        self.flight = 0                   # concurrent handlers right now
        self.max_flight = 0
        self.responses: list[int] = []
        self.response_index = 0
        self.unblock_first: threading.Event | None = None

    def next_status(self) -> int:
        with self.lock:
            if self.response_index < len(self.responses):
                status = self.responses[self.response_index]
            else:
                status = 200
            self.response_index += 1
            return status

    def record(self, body: dict) -> None:
        with self.lock:
            self.envelopes.append(body)

        if self.unblock_first is not None and len(self.envelopes) == 1:
            self.unblock_first.wait(5.0)
        if self.delay:
            time.sleep(self.delay)
        if self.single_flight_probe:
            with self.lock:
                self.flight += 1
                self.max_flight = max(self.max_flight, self.flight)
            time.sleep(0.05)
            with self.lock:
                self.flight -= 1


def flatten(state: StubState) -> list[str]:
    out: list[str] = []
    for envelope in state.envelopes:
        batch = envelope["data"].get("batch")
        assert isinstance(batch, list), f"every flush must ride the batch envelope: {envelope['data']}"
        out.extend(c["args"]["text"] for c in batch)
    return out


def texts(state: StubState) -> list[list[str]]:
    return [[c["args"]["text"] for c in (e["data"].get("batch") or [])] for e in state.envelopes]


def make_stub(state: StubState) -> RealtimePublisher:
    stub = state

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            body = json.loads(self.rfile.read(length) or b"{}")
            status = stub.next_status()
            state.record(body)
            # error-shaped envelopes for non-2xx — the real server's contract
            # (docs/CONTRACT.md §2), which publisher._call maps onto codes
            if status == 200:
                payload = json.dumps({
                    "ok": True, "user_id": "u", "group": "g", "partition": "p",
                    "connection_id": "c", "connection_partition": "p",
                }).encode()
            else:
                error_code = {
                    404: "connection_not_found",
                    403: "connection_not_in_partition",
                }.get(status, "request_failed")
                payload = json.dumps({
                    "error": error_code,
                    "detail": f"status {status}",
                }).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            if status == 429:
                self.send_header("Retry-After", "1")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *args):
            pass

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        probe.listen(16)
        port = probe.getsockname()[1]

    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()

    return RealtimePublisher(RealtimePublisherOptions(
        base_url=f"http://127.0.0.1:{port}",
        token_provider=StaticTokenProvider("coalesce-bearer"),
        user_agent_suffix="pytest-coalesce",
    ))


def chunk(text: str) -> dict:
    return {"type": "reclassifyThinking", "args": {"text": text}}


def offer_all(handle, items) -> float:
    started = time.perf_counter()
    for item in items:
        handle.offer(item)
    return time.perf_counter() - started


# ── gates ────────────────────────────────────────────────────────────────────

def test_pc1_offer_never_blocks_on_http():
    """offer() is validate+enqueue only: four offers land well under 10 ms while
    the first publish round-trip is deliberately suspended server-side (§6.1)."""
    state = StubState(delay=0.6)
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u1")
    handle.offer(chunk("first"))                  # starts the in-flight publish
    time.sleep(0.3)                               # let flush #1 detach + go in flight

    elapsed = offer_all(handle, [chunk("a"), chunk("b"), chunk("c")])
    stats = handle.drain()                        # merges whatever queued + waits

    assert stats.offered == 4
    assert stats.publishes == 2 and stats.flushed == 4 and stats.dropped == 0
    assert stats.last_error is None
    assert elapsed < 0.05, f"offer() blocked the hot path: {elapsed:.4f}s for 4 offers"
    assert texts(state)[0] == ["first"]
    assert texts(state)[1] == ["a", "b", "c"]     # offered-during-flight ride one flush


def test_pc2_single_flight_with_slow_server():
    """The stub records max concurrent handlers — must stay 1 forever (§6.2)."""
    state = StubState(delay=0.05, single_flight_probe=True)
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u2")

    for name in ("c1", "c2", "c3", "c4"):
        handle.offer(chunk(name))
        time.sleep(0.03)

    stats = handle.drain()
    assert stats.dropped == 0 and stats.last_error is None
    assert state.max_flight == 1, f"concurrent publishes observed: {state.max_flight}"


def test_pc3_order_matches_offer_order():
    state = StubState(delay=0.02)
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u3")

    items = [chunk(f"n{i:02d}") for i in range(40)]
    for item in items:
        handle.offer(item)
    stats = handle.drain()

    assert stats.flushed == 40 and stats.dropped == 0
    assert flatten(state) == [f"n{i:02d}" for i in range(40)]


def test_pc4_drain_stats_and_last_error_propagation():
    """drain() flushes the remainder, propagates the last failed batch ONCE
    (spec §3.2 'propagate last error'), then serves idempotent stats and
    rejects further offers (§3.2)."""
    state = StubState()
    state.responses = [409]                       # first flush fails (non-429), 200s after
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u4")

    handle.offer(chunk("d1"))
    handle.offer(chunk("d2"))
    time.sleep(0.3)                               # let flush #1 fail
    handle.offer(chunk("succeeds"))

    from pytest import raises as raises
    with raises(RealtimeApiError) as caught:
        handle.drain()
    assert caught.value.status == 409

    assert texts(state) == [["d1", "d2"], ["succeeds"]]     # flush order on the wire
    stats = handle.drain()                        # idempotent readout, no second raise
    assert stats.publishes == 1 and stats.flushed == 1
    assert stats.dropped == 2                     # the failed batch
    assert isinstance(stats.last_error, RealtimeApiError)
    assert stats.last_error.status == 409

    with pytest.raises(RealtimeValidationError):
        handle.offer(chunk("too late"))


def test_pc5_four29_honors_retry_then_drops_after_one_retry():
    # scenario A: 429 on flush #1 — same batch retried after Retry-After, intact
    state_a = StubState()
    state_a.responses = [429, 200, 200]
    publisher_a = make_stub(state_a)
    handle_a = publisher_a.coalesced_send_to_user("u5")
    handle_a.offer(chunk("r1"))
    handle_a.offer(chunk("r2"))
    time.sleep(0.2)                               # force the first flush boundary
    offer_all(handle_a, [chunk("r3")])
    handle_a.offer(chunk("r4"))
    asserted_stats = handle_a.drain()

    assert texts(state_a) == [["r1", "r2"], ["r1", "r2"], ["r3", "r4"]]
    #                         attempt   retry@+1s     flush #2 rides the next window
    assert asserted_stats.publishes == 2 and asserted_stats.dropped == 0
    assert asserted_stats.last_error is None                 # recovered 429 is not an error state

    # scenario B: 429 twice on one batch — one retry, then drop, stream continues
    state_b = StubState()
    state_b.responses = [429, 429, 200, 200, 200]
    publisher_b = make_stub(state_b)
    handle_b = publisher_b.coalesced_send_to_user("u5b")
    handle_b.offer(chunk("g1"))
    handle_b.offer(chunk("g2"))
    time.sleep(0.15)                              # flush#1: 429 → retry-after 1s → retry
    handle_b.offer(chunk("g3"))                   # rides the flush after the drop

    from pytest import raises as raises
    with raises(RealtimeApiError) as caught_b:
        handle_b.drain()
    assert caught_b.value.status == 429           # the dropped batch propagates

    stats_b = handle_b.drain()                    # idempotent readout
    assert stats_b.dropped == 2 and stats_b.flushed == 1
    assert stats_b.publishes == 1
    assert texts(state_b) == [["g1", "g2"], ["g1", "g2"], ["g3"]]
    #                          failed      retry@+1s       batch after the drop
    assert isinstance(stats_b.last_error, RealtimeApiError)


def test_pc6_max_pending_chars_bounds_memory_drop_oldest():
    """While flush #1 hangs server-side, offers pile past the budget → the
    OLDEST pending chunks are shed (newest reasoning wins), counted (§3.4)."""
    state = StubState()
    state.unblock_first = threading.Event()
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u6", max_pending_chars=40)

    handle.offer(chunk("held"))                   # flush #1: suspends on the stub
    time.sleep(0.3)                               # detach 'held' so the pile sheds only the pile
    piled = [chunk(f"p{i:02d}" + "y" * 10) for i in range(10)]   # ~40 chars each
    offer_all(handle, piled)
    time.sleep(0.3)

    state.unblock_first.set()                     # release the hanging round-trip
    stats = handle.drain()

    total = 1 + 10
    assert stats.offered == total
    assert stats.dropped > 0, "budget must shed something with 400 chars pending"
    assert stats.flushed + stats.dropped == total
    order = flatten(state)
    assert order[0] == "held"                     # the in-flight flush is unaffected
    tail = order[1:]
    assert tail == [c["args"]["text"] for c in piled[-len(tail):]]   # newest survive


def test_pc7_batch_envelope_wire_shape():
    state = StubState()
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("u7")

    handle.offer(chunk("w1"))
    handle.offer(chunk("w2"))
    time.sleep(0.5)                               # flush #1 lands: [w1, w2]
    handle.offer(chunk("w3"))
    handle.drain()

    assert texts(state) == [["w1", "w2"], ["w3"]]          # batch line + remainder
    first = state.envelopes[0]["data"]
    assert first["type"] == "reclassifyThinking"           # outer identity = stream type
    assert first["batch_v"] == 1
    assert first["batch"][0] == chunk("w1")                # chunks ride verbatim


def test_offer_validates_type_eagerly_like_send_lanes():
    state = StubState()
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_user("v1")
    with pytest.raises(RealtimeValidationError):
        handle.offer({"args": "no type"})
    with pytest.raises(RealtimeValidationError):
        handle.offer("not even a dict")
    assert handle.drain().offered == 0
    assert state.envelopes == []                  # nothing ever hit the wire


# ── PC-8: the connection lane (per-tab CoT streams) ──────────────────────────

def test_pc8a_connection_lane_carries_order_and_envelope():
    """PC-8a mirror of PC-3/PC-7 through POST /api/connections/{id}/send:
    independent per-TAB stream — order + batch envelope identical to user lane."""
    state = StubState()
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_connection("tab-1")
    handle.offer(chunk("t1.1"))
    handle.offer(chunk("t1.2"))
    time.sleep(0.5)                               # flush #1: [t1.1, t1.2]
    handle.offer(chunk("t1.3"))
    stats = handle.drain()

    assert stats.publishes == 2 and stats.flushed == 3 and stats.dropped == 0
    assert texts(state) == [["t1.1", "t1.2"], ["t1.3"]]
    first = state.envelopes[0]["data"]
    assert first["type"] == "reclassifyThinking" and first["batch_v"] == 1


def test_pc8b_connection_lane_dead_socket_drops_without_hang():
    """PC-8b: 404 connection_not_found (tab closed before the flush lands) is a
    NON-429 failure: same-batch-once retry must NOT happen — the batch drops,
    the stream continues, drain propagates exactly once."""
    state = StubState()
    state.responses = [404, 200]                  # stream 1 dies; the next flush succeeds
    publisher = make_stub(state)
    handle = publisher.coalesced_send_to_connection("gone-tab")
    handle.offer(chunk("c1"))
    handle.offer(chunk("c2"))
    time.sleep(0.3)                               # flush #1 fails 404 (NO retry — one attempt only)
    handle.offer(chunk("survivor-1"))             # stream keeps going on the same handle

    with pytest.raises(RealtimeApiError) as caught:
        handle.drain()
    assert caught.value.status == 404
    assert caught.value.code == "connection_not_found"

    stats = handle.drain()
    assert stats.publishes == 1 and stats.flushed == 1 and stats.dropped == 2
    # one wire attempt per flush — no same-batch retry on 404
    assert texts(state) == [["c1", "c2"], ["survivor-1"]]


def test_pc8c_connection_lane_rejects_bad_targets_eagerly():
    state = StubState()
    publisher = make_stub(state)
    with pytest.raises(RealtimeValidationError):
        publisher.coalesced_send_to_connection("")          # the lane validation is eager
    with pytest.raises(RealtimeValidationError):
        publisher.coalesced_send_to_connection("   ")
    assert state.envelopes == []
