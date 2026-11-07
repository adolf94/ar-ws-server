"""RealtimePublisher — the publisher doors over HTTP (CONTRACT v1).

All scoping derives from the bearer's client_id (it is NEVER accepted from a
body); from/sent_at are server-stamped. The Cloudflare lane sends an honest
User-Agent on every request — override it via RealtimePublisherOptions if
your app wants its own name.
"""

from __future__ import annotations

from typing import Any, Callable, Optional, Sequence
from urllib.parse import quote

import httpx

from .errors import RealtimeApiError, RealtimeValidationError
from .models import (
    BroadcastResult,
    DisconnectResult,
    GroupCreateResult,
    GroupRow,
    HistoryClearResult,
    HistoryResult,
    MemberAddResult,
    MemberRemoveResult,
    PresenceRow,
    PublishResult,
    SendResult,
)
from .tokens import AccessTokenProvider, CallableTokenProvider

DEFAULT_USER_AGENT = f"ar-realtime-client/0.1.0"


class RealtimePublisherOptions:
    """Base URL + tokens. Callers own the IdP minting (ar-auth-client)."""

    def __init__(
        self,
        base_url: str,
        token_provider: AccessTokenProvider | Callable[[], str],
        user_agent_suffix: Optional[str] = None,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.user_agent_suffix = user_agent_suffix
        # accept a .get()-style provider, a 0-arg callable factory,
        # or an AccessTokenProvider — nothing else (a raw dict/str would
        # otherwise surface as a garbage bearer at call time)
        provider = token_provider
        if isinstance(provider, (str, bytes, dict)):
            raise RealtimeValidationError(
                "token_provider must expose .get() or be a 0-arg callable "
                f"— got a raw {type(provider).__name__}"
            )
        if hasattr(provider, "get"):
            self.token_provider = provider
        elif callable(provider):
            self.token_provider = CallableTokenProvider(provider)
        else:
            raise RealtimeValidationError(
                "token_provider must expose .get() or be a 0-arg callable "
                f"— got {type(provider).__name__}"
            )


class RealtimePublisher:
    def __init__(self, options: RealtimePublisherOptions, client: Optional[httpx.Client] = None) -> None:
        self._options = options
        self._client = client or httpx.Client(
            follow_redirects=False,
            headers={"User-Agent": self._user_agent()},
        )
        self._owns_client = client is None

    def _user_agent(self) -> str:
        suffix = self._options.user_agent_suffix or "ar-auth publisher"
        return f"{DEFAULT_USER_AGENT} ({suffix})"

    # ── send lanes (the reasoning stream addresses) ──────────────────────────
    def publish(self, group: str, data: dict[str, Any]) -> PublishResult:
        body = self._call("POST", "/api/publish", {"group": group, "data": self._envelope(data)})
        return PublishResult(body["ok"], body["group"], body["partition"])

    def broadcast(self, groups: Sequence[str], data: dict[str, Any]) -> BroadcastResult:
        named = [g for g in (groups or []) if g]
        if not named:
            raise RealtimeValidationError("groups needs at least one group name")
        body = self._call("POST", "/api/broadcast", {"groups": named, "data": self._envelope(data)})
        return BroadcastResult(
            body["ok"], body["partition"], body["groups_delivered"], body["unknown_groups"]
        )

    def send_to_user(self, user_sub: str, data: dict[str, Any]) -> SendResult:
        """cross-service unicast over user_channel|{sub}; NOT partition-scoped."""
        body = self._call("POST", f"/api/users/{quote(str(user_sub), safe='')}/send", {"data": self._envelope(data)})
        return SendResult(ok=body["ok"], user_id=body["user_id"])

    def send_to_connection(self, connection_id: str, data: dict[str, Any]) -> SendResult:
        body = self._call("POST", f"/api/connections/{quote(str(connection_id), safe='')}/send",
                          {"data": self._envelope(data)})
        return SendResult(ok=body["ok"], connection_id=body["connection_id"],
                          connection_partition=body["connection_partition"])

    def disconnect(self, connection_id: str) -> DisconnectResult:
        body = self._call("POST", f"/api/connections/{quote(str(connection_id), safe='')}/disconnect")
        return DisconnectResult(body["ok"], body["connection_id"], body["connection_partition"], body["aborted"])

    # ── group ops ────────────────────────────────────────────────────────────
    def create_group(
        self,
        name: str,
        history: Optional[bool] = None,
        history_size: Optional[int] = None,
        history_ttl_seconds: Optional[int] = None,
    ) -> GroupCreateResult:
        if not name or not name.strip():
            raise RealtimeValidationError("name is required")
        body = self._call("POST", "/api/groups", {
            "name": name,
            "history": history,
            "history_size": history_size,
            "history_ttl_seconds": history_ttl_seconds,
        })
        return GroupCreateResult(
            body["ok"], body["created"], body["group"], body["partition"], body["history_enabled"]
        )

    def add_member(self, group: str, user_id: str) -> MemberAddResult:
        self._require_member(user_id)
        body = self._call("POST", f"/api/groups/{quote(str(group), safe='')}/members", {"user_id": user_id})
        return MemberAddResult(
            body["ok"], body["group"], body["group_partition"],
            body["member"]["user_id"], body["member"]["client_id"], body["connections_joined"],
        )

    def remove_member(self, group: str, user_id: str) -> MemberRemoveResult:
        self._require_member(user_id)
        body = self._call("DELETE",
                          f"/api/groups/{quote(str(group), safe='')}/members?user_id={quote(str(user_id), safe='')}")
        return MemberRemoveResult(
            body["ok"], body["group"], body["group_partition"],
            body["member"]["user_id"], body["member"]["client_id"], body["removed"],
        )

    def list_groups(self) -> list[GroupRow]:
        body = self._call("GET", "/api/groups")
        return [GroupRow(row["group"], row["members"], row["online"]) for row in body["groups"]]

    def presence(self, group: str) -> list[PresenceRow]:
        body = self._call("GET", f"/api/groups/{quote(str(group), safe='')}/presence")
        return [PresenceRow(row["user_id"], row["client_id"], row["online"], row["connections"])
                for row in body["presence"]]

    def history(self, group: str, limit: Optional[int] = None) -> HistoryResult:
        path = f"/api/groups/{quote(str(group), safe='')}/history"
        if limit is not None:
            path += f"?limit={limit}"
        body = self._call("GET", path)
        return HistoryResult(
            body["ok"], body["group"], body["group_partition"],
            body["history_enabled"], body["total"], body["history"],
        )

    def clear_history(self, group: str) -> HistoryClearResult:
        body = self._call("DELETE", f"/api/groups/{quote(str(group), safe='')}/history")
        return HistoryClearResult(body["ok"], body["group"], body["group_partition"], body["cleared"])

    # ── plumbing ─────────────────────────────────────────────────────────────
    @staticmethod
    def _envelope(data: dict[str, Any]) -> dict[str, Any]:
        """CONTRACT §3: the event's IDENTITY lives in data.type."""
        if not isinstance(data, dict) or not isinstance(data.get("type"), str):
            raise RealtimeValidationError(
                "data.type is required — it is the event-identity wire convention"
            )
        return data

    @staticmethod
    def _require_member(user_id: str) -> None:
        if not user_id or not user_id.strip():
            raise RealtimeValidationError("user_id is required")

    def _call(self, method: str, path: str, payload: Optional[dict[str, Any]] = None) -> dict[str, Any]:
        headers = {"Authorization": f"Bearer {self._options.token_provider.get()}"}
        try:
            response = self._client.request(method, self._options.base_url + path,
                                            json=payload, headers=headers)
        except httpx.HTTPError as error:  # transport-level, keep it loud
            raise RealtimeApiError(0, "transport_error", str(error)) from error

        raw = response.text
        if response.status_code >= 400:
            code, detail = response.reason_phrase, raw
            try:
                body = response.json()
                code = body.get("error", code)
                detail = body.get("detail")
            except ValueError:
                pass
            retry_after = None
            if response.status_code == 429:
                header = response.headers.get("retry-after")
                if header and header.isdigit() and int(header) > 0:
                    retry_after = int(header)
            raise RealtimeApiError(response.status_code, str(code), detail, retry_after)

        try:
            return response.json()
        except ValueError as error:  # empty/invalid body on a 2xx is contract drift
            raise RealtimeApiError(response.status_code, "body_invalid", raw) from error

    def close(self) -> None:
        if self._owns_client:
            self._client.close()

    def __enter__(self) -> "RealtimePublisher":
        return self

    def __exit__(self, *_exc: object) -> None:
        self.close()
