"""Consumer vs publisher: this package is the PUBLISHER door (HTTP API).

The hub (bus) side is read-only per contract — SPA/Node consumers belong to
@adolf94/realtime-client (clients/ts).
"""

from .coalescing import CoalesceStats, CoalescedSendHandle
from .errors import RealtimeApiError, RealtimeClientError, RealtimeValidationError
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
from .publisher import DEFAULT_USER_AGENT, RealtimePublisher, RealtimePublisherOptions
from .tokens import (  # type: ignore[assignment]
    AccessTokenProvider,
    ArAuthTokenProvider,
    CallableTokenProvider,
    StaticTokenProvider,
)

__all__ = [
    "RealtimePublisher",
    "RealtimePublisherOptions",
    "DEFAULT_USER_AGENT",
    "CoalesceStats",
    "CoalescedSendHandle",
    "RealtimeApiError",
    "RealtimeClientError",
    "RealtimeValidationError",
    "BroadcastResult",
    "DisconnectResult",
    "GroupCreateResult",
    "GroupRow",
    "HistoryClearResult",
    "HistoryResult",
    "MemberAddResult",
    "MemberRemoveResult",
    "PresenceRow",
    "PublishResult",
    "SendResult",
    "AccessTokenProvider",
    "ArAuthTokenProvider",
    "CallableTokenProvider",
    "StaticTokenProvider",
]

__version__ = "0.1.1"
