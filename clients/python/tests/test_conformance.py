"""Publisher conformance — mirrors clients/dotnet's PB gates.

Runs against the mock lane via dev/test-clients.sh python. Self-skips when
SRV is unset so a bare pytest stays green.
"""

from __future__ import annotations

import os
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler

import httpx
import pytest

from ar_realtime_client import (
    RealtimeApiError,
    RealtimePublisher,
    RealtimePublisherOptions,
    RealtimeValidationError,
    StaticTokenProvider,
)

SRV = os.environ.get("SRV", "")
MOCK_AUTH = os.environ.get("MOCK_AUTH", "http://localhost:9001")
PUBLISHER_SCOPE = "api://ws-server/app:publisher"


def mint(client_id: str, scope: str) -> str:
    response = httpx.post(f"{MOCK_AUTH}/token", json={
        "sub": "svc", "scope": scope, "client_id": client_id,
    })
    response.raise_for_status()
    return response.json()["access_token"]


@pytest.fixture
def publisher() -> RealtimePublisher:
    if not SRV:
        pytest.skip("no SRV — harness (dev/test-clients.sh python) provides the stack")
    return RealtimePublisher(RealtimePublisherOptions(
        base_url=SRV,
        token_provider=StaticTokenProvider(mint("python-test", PUBLISHER_SCOPE)),
        user_agent_suffix="pytest-conformance",
    ))


@pytest.fixture
def rate_publisher() -> RealtimePublisher:
    return RealtimePublisher(RealtimePublisherOptions(
        base_url=SRV,
        token_provider=StaticTokenProvider(mint("rate-python", PUBLISHER_SCOPE)),
    ))


def test_pb1_publish_returns_partition(publisher):
    result = publisher.publish("py-demo", {"type": "chat_receive", "text": "from the python client"})
    assert result.ok and result.group == "py-demo" and result.partition == "python-test"


def test_pb2_broadcast_reports_unknown_groups(publisher):
    publisher.create_group("py-demo")
    publisher.create_group("py-demo2")
    result = publisher.broadcast(["py-demo", "py-demo2", "ghost-group"],
                                 {"type": "chat_receive", "text": "b"})
    assert result.groups_delivered == ["py-demo", "py-demo2"]
    assert result.unknown_groups == ["ghost-group"]
    assert result.partition == "python-test"


def test_pb3_send_lanes_map_shapes_and_errors(publisher):
    user = publisher.send_to_user("42", {"type": "notify_receive", "text": "u"})
    assert user.user_id == "42"

    with pytest.raises(RealtimeApiError) as ghost:
        publisher.send_to_connection("no-such-conn", {"type": "notify_receive", "text": "x"})
    assert ghost.value.status == 404
    assert ghost.value.code == "connection_not_found"

    with pytest.raises(RealtimeValidationError) as untyped:
        publisher.publish("py-demo", {"text": "no type"})
    assert "data.type" in str(untyped.value)


def test_pb4_group_create_is_idempotent_with_history_opt_in(publisher):
    first = publisher.create_group("py-hist", history=True, history_size=5)
    assert first.created and first.history_enabled

    second = publisher.create_group("py-hist")
    assert not second.created
    assert second.history_enabled


def test_pb5_members_listing_presence_history_clear(publisher):
    add = publisher.add_member("py-hist", "42")
    assert add.member_client_id == "python-test"
    assert add.connections_joined == 0
    assert publisher.remove_member("py-hist", "43").removed is False

    assert any(row.group == "py-hist" for row in publisher.list_groups())

    rows = publisher.presence("py-hist")
    assert len(rows) == 1 and rows[0].user_id == "42" and rows[0].online is False

    publisher.publish("py-hist", {"type": "chat_receive", "text": "ring entry"})
    history = publisher.history("py-hist", limit=10)
    assert history.total == 1

    assert publisher.clear_history("py-hist").cleared == 1
    assert publisher.history("py-hist").total == 0


def test_pb6_disconnect_unknown_maps_404(publisher):
    with pytest.raises(RealtimeApiError) as ghost:
        publisher.disconnect("no-such-conn")
    assert ghost.value.status == 404
    assert ghost.value.code == "connection_not_found"


def test_pb7_rate_limit_maps_429_envelope_with_retry_after(rate_publisher, publisher):
    rate = None
    for _ in range(120):
        try:
            rate_publisher.publish("ghost-group", {"type": "chat_receive", "text": "burst"})
        except RealtimeApiError as error:
            if error.status == 429:
                rate = error
                break
    assert rate is not None, "expected the fixed window to trip within 120 calls"
    assert rate.code == "rate_limited"
    assert rate.retry_after and rate.retry_after > 0
    # other partition untouched
    assert publisher.publish("py-demo", {"type": "chat_receive", "text": "probe"}).ok


def test_ua1_user_agent_is_ours():
    observed: dict[str, str] = {}

    class Echo(BaseHTTPRequestHandler):
        def do_POST(self):
            observed["user_agent"] = self.headers.get("User-Agent", "")
            observed["authorization"] = self.headers.get("Authorization", "")
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b"{}")

        def log_message(self, *args):
            pass

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        probe.listen(1)
        port = probe.getsockname()[1]

    import http.server
    server = http.server.HTTPServer(("127.0.0.1", port), Echo)
    thread = threading.Thread(target=server.handle_request, daemon=True)
    thread.start()
    publisher = RealtimePublisher(RealtimePublisherOptions(
        base_url=f"http://127.0.0.1:{port}",
        token_provider=StaticTokenProvider("echo-bearer"),
        user_agent_suffix="pytest-ua",
    ))
    with pytest.raises(RealtimeApiError):
        publisher.publish("whatever", {"type": "chat_receive", "text": "ua"})
    thread.join(timeout=5)
    server.server_close()
    assert "ar-realtime-client" in observed["user_agent"]
    assert observed["authorization"] == "Bearer echo-bearer"
