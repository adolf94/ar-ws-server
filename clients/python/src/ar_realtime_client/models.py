"""Publisher models — mirror of the wire contract (realtime docs/CONTRACT.md §4)."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional


@dataclass(frozen=True)
class RealtimeEvent:
    """The bus envelope: identity in data.type, server-stamped provenance."""

    type: str
    from_: str
    sent_at: str
    payload: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class PublishResult:
    ok: bool
    group: str
    partition: str


@dataclass(frozen=True)
class BroadcastResult:
    ok: bool
    partition: str
    groups_delivered: list[str]
    unknown_groups: list[str]


@dataclass(frozen=True)
class GroupCreateResult:
    ok: bool
    created: bool
    group: str
    partition: str
    history_enabled: bool


@dataclass(frozen=True)
class MemberAddResult:
    ok: bool
    group: str
    group_partition: str
    user_id: str
    member_client_id: str
    connections_joined: int


@dataclass(frozen=True)
class MemberRemoveResult:
    ok: bool
    group: str
    group_partition: str
    user_id: str
    member_client_id: str
    removed: bool


@dataclass(frozen=True)
class GroupRow:
    group: str
    members: int
    online: int


@dataclass(frozen=True)
class PresenceRow:
    user_id: str
    client_id: str
    online: bool
    connections: int


@dataclass(frozen=True)
class HistoryResult:
    ok: bool
    group: str
    group_partition: str
    history_enabled: bool
    total: int
    items: list[Any]


@dataclass(frozen=True)
class HistoryClearResult:
    ok: bool
    group: str
    group_partition: str
    cleared: int


@dataclass(frozen=True)
class SendResult:
    ok: bool
    user_id: Optional[str] = None
    connection_id: Optional[str] = None
    connection_partition: Optional[str] = None


@dataclass(frozen=True)
class DisconnectResult:
    ok: bool
    connection_id: str
    connection_partition: str
    aborted: bool
