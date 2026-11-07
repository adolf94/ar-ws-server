"""Token plumbing (owner spec: depend on ar-auth-client-*).

The transport stays auth-agnostic; providers adapt an Ar.Auth client into
the bearer-per-call requirement of the realtime publisher surface.
"""

from __future__ import annotations

import time
from typing import Callable, Optional, Protocol


class AccessTokenProvider(Protocol):
    def get(self) -> str:
        """Return an IdP bearer — fresh, or a still-valid cached one."""
        ...


class StaticTokenProvider:
    """One value for everything — the caller vends a current token."""

    def __init__(self, token: str) -> None:
        self._token = token

    def get(self) -> str:
        return self._token


class CallableTokenProvider:
    """The caller's factory; invoked per API call (e.g. a refresh-aware one)."""

    def __init__(self, factory: Callable[[], str]) -> None:
        self._factory = factory

    def get(self) -> str:
        return self._factory()


class ArAuthTokenProvider:
    """Adapts the sibling ``ar_auth.ArAuthClient`` (ar-auth-client-python).

    Uses ``client_credentials`` production caching until (expires_in - 60 s),
    re-requesting thereafter — mirrors Ar.Realtime.Client's
    ``ArAuthAccessTokenProvider``. Construct with an ArAuthClient carrying
    ClientId/ClientSecret; the requested scope must cover PUBLISH_REQUIRED_SCOPE.

    Requires the ``ar-auth`` extra (`pip install ar-realtime-client[ar-auth]`).
    """

    def __init__(self, ar_auth_client: Any, scope: Optional[str] = None) -> None:
        self._auth = ar_auth_client
        self._scope = scope
        self._cached: Optional[str] = None
        self._until = 0.0  # monotonic seconds

    def get(self) -> str:
        if self._cached is not None and time.monotonic() < self._until:
            return self._cached
        response = self._auth.client_credentials(self._scope)
        token = response["access_token"]
        expires_in = int(response.get("expires_in") or 0)
        # re-mint a minute early — clock skew is free; mid-flight 401s are not
        self._until = time.monotonic() + max(30.0, float(expires_in) - 60.0)
        self._cached = str(token)
        return self._cached
