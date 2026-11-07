"""Errors mapped from the CONTRACT §2 wire envelope and client-side validation."""

from __future__ import annotations

from typing import Optional


class RealtimeClientError(Exception):
    """Base — including client-side sanity failures (no wire traffic happened)."""


class RealtimeValidationError(RealtimeClientError):
    """Raised BEFORE any wire traffic (missing data.type, ids, …)."""


class RealtimeApiError(RealtimeClientError):
    """A non-2xx from the API surface.

    Attributes:
        status: HTTP status code.
        code: the envelope's ``error`` value (e.g. ``group_not_found``).
        detail: envelope ``detail`` hint.
        retry_after: seconds until the client_id rate window rolls over —
            populated ONLY on 429 (rate_limited, CONTRACT §2.1).
    """

    def __init__(
        self,
        status: int,
        code: str,
        detail: Optional[str] = None,
        retry_after: Optional[int] = None,
    ) -> None:
        super().__init__(f"{status} {code}: {detail}")
        self.status = status
        self.code = code
        self.detail = detail
        self.retry_after = retry_after
