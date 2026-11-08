"""Coalesced send-to-user — single-flight publish coalescing (handover §3).

One handle owns ONE stream to `user_channel|{sub}` and, on the wire, AT MOST
ONE outstanding `POST /api/users/{sub}/send` at any moment:

  * `offer()` never touches HTTP — lock + append + return (≤1 ms-class),
    so the producer hot path (model emitting chunks) is never gated by RTT;
  * chunks offered while a request is in flight ride the NEXT flush, which
    fires the instant the response lands — batch size self-tunes to
    `generation_rate × RTT` with zero timers;
  * strict ordering falls out of single-flight: request N+1 is never sent
    before N's response (pipelining would reorder on connection failure);
  * the batch rides the CONTRACT §3 envelope with the stream's `type` as
    identity plus `batch` (chunks verbatim) and `batch_v: 1`;
  * 429 (`rate_limited`, CONTRACT §2.1): honor `Retry-After` and retry the
    SAME batch once; a second 429 (or any other failure) drops the batch,
    records it, and keeps the stream going;
  * `max_pending_chars` bounds memory of queued-but-unflushed chunks by
    shedding the OLDEST ones — freshest reasoning wins.

`drain()` (call at stream end / on context exit): flushes the remainder,
propagates the last error (RealtimeApiError — offer() rejects afterwards).
"""

from __future__ import annotations

import json
import threading
import time
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Optional

from .errors import RealtimeApiError, RealtimeValidationError

__all__ = ["CoalesceStats", "CoalescedSendHandle"]

BATCH_V = 1
DEFAULT_MAX_PENDING_CHARS = 64_000


@dataclass
class CoalesceStats:
    """Washout counters of one coalesced handle (chunks, not bytes)."""

    offered: int = 0          # validated chunks accepted (post-validation)
    flushed: int = 0          # chunks delivered to the bus
    dropped: int = 0          # shed: overflow (drop-oldest) or failed flushes
    publishes: int = 0        # SUCCESSFUL flush round-trips
    last_error: Optional[Exception] = field(default=None, repr=False)


class CoalescedSendHandle:
    """Single-flight coalescer for one `user_channel|{sub}` stream."""

    def __init__(
        self,
        publisher: "RealtimePublisher",   # type: ignore[name-defined]  # noqa: F821
        user_sub: str,
        max_pending_chars: int = DEFAULT_MAX_PENDING_CHARS,
    ) -> None:
        if not isinstance(user_sub, str) or not user_sub.strip():
            raise RealtimeValidationError("user_sub is required")
        if max_pending_chars < 1:
            raise RealtimeValidationError("max_pending_chars must be positive")

        self._publisher = publisher
        self._user_sub = user_sub
        self._max_pending_chars = max_pending_chars

        self._cond = threading.Condition()
        self._pending: list[dict[str, Any]] = []
        self._pending_chars = 0
        self._stream_type: Optional[str] = None
        self._inflight = False
        self._draining = False            # set by drain(): offer() rejects after
        self._error_surfaced = False      # one drain() raise, then stats-only
        self._finished = False            # worker exited
        self._stats = CoalesceStats()

        self._worker = threading.Thread(
            target=self._work, name="ar-realtime-coalesce", daemon=True
        )
        self._worker.start()

    # ── producer hot path ────────────────────────────────────────────────────
    def offer(self, data: dict[str, Any]) -> None:
        """Enqueue one chunk. Validates the CONTRACT §3 identity first (like the
        send lanes), then lock+append+return — never touches HTTP."""
        if not isinstance(data, dict) or not isinstance(data.get("type"), str):
            raise RealtimeValidationError(
                "data.type is required — it is the event-identity wire convention"
            )

        with self._cond:
            if self._draining:
                raise RealtimeValidationError(
                    "coalesce handle is drained — open a new one per stream"
                )
            self._accept(data)
            self._cond.notify_all()

    # ── stream end ───────────────────────────────────────────────────────────
    def drain(self) -> CoalesceStats:
        """Flush the remainder and wait for the in-flight request to land.

        Propagates the last failed batch as RealtimeApiError exactly once;
        subsequent `drain()` calls return the same stats idempotently.
        """
        with self._cond:
            self._draining = True
            self._cond.notify_all()
            while not self._finished:
                self._cond.wait()

        raise_on_error = not self._error_surfaced
        with self._cond:
            self._error_surfaced = True
            stats = self._stats
            error = stats.last_error if raise_on_error else None

        if isinstance(error, BaseException):
            raise error
        return stats

    # ── worker: the single publisher lane ────────────────────────────────────
    def _work(self) -> None:
        # the finally clause is the drain() no-hang guarantee: WHATEVER happens
        # (stub bug, transport glitch, KeyboardInterrupt), the waiter resolves
        try:
            while True:
                with self._cond:
                    # exit only once the stream is drained AND nothing outstanding
                    while not self._pending and not self._draining:
                        self._cond.wait()
                    if self._draining and not self._pending and not self._inflight:
                        break
                    batch, self._pending = self._pending, []
                    self._pending_chars = 0
                    self._inflight = True

                try:
                    self._flush(batch)
                finally:
                    with self._cond:
                        self._inflight = False
                        self._cond.notify_all()

                with self._cond:
                    if self._draining and not self._pending and not self._inflight:
                        break
        finally:
            with self._cond:
                self._finished = True
                self._cond.notify_all()

    def _flush(self, batch: list[dict[str, Any]]) -> bool:
        """One wire publish of the whole batch: 429 → wait+retry once, else
        record + drop. Deliberately NO pipelining: this returns before the
        next flush, which is the ordering guarantee."""
        with self._cond:
            stream_type = self._stream_type or batch[0]["type"]
            self._stream_type = stream_type

        envelope: dict[str, Any] = {"type": stream_type, "batch": batch, "batch_v": BATCH_V}

        attempt = 0
        while True:
            try:
                self._publisher.send_to_user(self._user_sub, envelope)
            except RealtimeApiError as error:
                if error.status == 429 and attempt == 0:
                    # wait out the window and re-send the SAME batch once
                    attempt = 1
                    time.sleep(max(error.retry_after or 60, 0))
                    continue
                self._record_error(error, len(batch))
                return False
            except Exception as error:   # non-API strangeness — drop the batch
                # loudly rather than kill the worker thread (drain() must never
                # hang waiting on a dead lane)
                self._record_error(error, len(batch))
                return False
            with self._cond:
                self._stats.flushed += len(batch)
                self._stats.publishes += 1
                self._cond.notify_all()
            return True

    def _record_error(self, error: Exception, chunk_count: int) -> None:
        with self._cond:
            self._stats.dropped += chunk_count
            self._stats.last_error = error
            self._cond.notify_all()

    def _accept(self, data: dict[str, Any]) -> None:
        """Enqueue + enforce the pending-chars budget: shed OLDEST first so the
        freshest reasoning survives a burst (drop-oldest; newest always wins,
        even when it alone exceeds the budget)."""
        chunk_chars = len(json.dumps(data, default=str))
        self._pending.append(data)
        self._stats.offered += 1
        self._pending_chars += chunk_chars
        while self._pending_chars > self._max_pending_chars and len(self._pending) > 1:
            oldest = self._pending.pop(0)
            self._pending_chars -= len(json.dumps(oldest, default=str))
            self._stats.dropped += 1

    # ── ergonomics ───────────────────────────────────────────────────────────
    def stats(self) -> CoalesceStats:
        with self._cond:
            return self._stats

    def __enter__(self) -> "CoalescedSendHandle":
        return self

    def __exit__(self, exc_type, _exc, _tb) -> None:
        self.drain()
