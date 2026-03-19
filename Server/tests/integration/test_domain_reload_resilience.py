"""
Integration test for domain reload resilience.

Tests that the MCP server can handle rapid-fire requests during Unity domain reloads
by waiting for the plugin to reconnect instead of failing immediately.
"""
import asyncio
import pytest
from unittest.mock import AsyncMock, patch
from datetime import datetime

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_plugin_hub_waits_for_reconnection_during_reload():
    """Test that PluginHub._resolve_session_id waits for plugin reconnection."""
    # Import after conftest stubs are set up
    from transport.plugin_hub import PluginHub
    from transport.plugin_registry import PluginRegistry, PluginSession

    # Create a mock registry
    mock_registry = AsyncMock(spec=PluginRegistry)

    # Simulate plugin reconnection sequence:
    # First 2 calls: no sessions (plugin disconnected)
    # Third call: session appears (plugin reconnected)
    call_count = [0]

    async def mock_list_sessions(**kwargs):
        call_count[0] += 1
        if call_count[0] <= 2:
            # Plugin not yet reconnected
            return {}
        else:
            # Plugin reconnected
            now = datetime.now()
            session = PluginSession(
                session_id="test-session-123",
                project_name="TestProject",
                project_hash="abc123",
                unity_version="2022.3.0f1",
                registered_at=now,
                connected_at=now
            )
            return {"test-session-123": session}

    mock_registry.list_sessions = mock_list_sessions

    # Configure PluginHub with our mock while preserving the original state
    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()

    try:
        # Call _resolve_session_id when no session is available
        # It should wait and retry until the session appears
        session_id = await PluginHub._resolve_session_id(unity_instance=None)

        # Should have retried and eventually found the session
        assert session_id == "test-session-123"
        assert call_count[0] >= 3  # Should have tried at least 3 times

    finally:
        # Clean up: restore original PluginHub state
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock


@pytest.mark.asyncio
async def test_plugin_hub_fails_after_timeout():
    """Test that PluginHub._resolve_session_id eventually times out if plugin never reconnects."""
    from transport.plugin_hub import PluginHub
    from transport.plugin_registry import PluginRegistry

    # Create a mock registry that never returns sessions
    mock_registry = AsyncMock(spec=PluginRegistry)

    async def mock_list_sessions(**kwargs):
        return {}  # Never returns sessions

    mock_registry.list_sessions = mock_list_sessions

    # Configure PluginHub with our mock while preserving the original state
    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()

    # Temporarily override config for a short timeout
    with patch('transport.plugin_hub.config') as mock_config:
        mock_config.reload_max_retries = 3  # Only 3 retries
        mock_config.reload_retry_ms = 10    # 10ms between retries

        try:
            # Should raise RuntimeError after timeout
            with pytest.raises(RuntimeError, match="No Unity plugins are currently connected"):
                await PluginHub._resolve_session_id(unity_instance=None)
        finally:
            # Clean up: restore original PluginHub state
            PluginHub._registry = original_registry
            PluginHub._lock = original_lock


@pytest.mark.asyncio
async def test_plugin_hub_no_wait_when_retry_disabled(monkeypatch):
    """retry_on_reload=False should skip reconnect wait loops."""
    from transport.plugin_hub import PluginHub, NoUnitySessionError
    from transport.plugin_registry import PluginRegistry

    mock_registry = AsyncMock(spec=PluginRegistry)
    mock_registry.get_session_id_by_hash = AsyncMock(return_value=None)
    mock_registry.list_sessions = AsyncMock(return_value={})

    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()

    monkeypatch.setenv("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", "20.0")

    try:
        with pytest.raises(NoUnitySessionError):
            await PluginHub._resolve_session_id(
                unity_instance="hash-missing",
                retry_on_reload=False,
            )

        assert mock_registry.get_session_id_by_hash.await_count == 1
        assert mock_registry.list_sessions.await_count == 1
    finally:
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock


@pytest.mark.asyncio
async def test_send_command_for_instance_fails_fast_on_stale_when_retry_disabled(monkeypatch):
    """Stale HTTP session should not send command when retry_on_reload is disabled."""
    from transport.plugin_hub import PluginHub

    resolve_mock = AsyncMock(return_value="sess-stale")
    ensure_mock = AsyncMock(return_value=False)
    send_mock = AsyncMock()

    monkeypatch.setattr(PluginHub, "_resolve_session_id", resolve_mock)
    monkeypatch.setattr(PluginHub, "_ensure_live_connection", ensure_mock)
    monkeypatch.setattr(PluginHub, "send_command", send_mock)

    result = await PluginHub.send_command_for_instance(
        unity_instance="Project@hash-stale",
        command_type="manage_script",
        params={"action": "edit"},
        retry_on_reload=False,
    )

    assert result["success"] is False
    assert result["hint"] == "retry"
    assert result.get("data", {}).get("reason") == "stale_connection"
    assert resolve_mock.await_count == 1
    _, resolve_kwargs = resolve_mock.await_args
    assert resolve_kwargs.get("retry_on_reload") is False
    send_mock.assert_not_awaited()


@pytest.mark.asyncio
async def test_read_console_during_simulated_reload(monkeypatch):
    """
    Simulate the stress test: create script (triggers reload) + rapid read_console calls.

    This test simulates what happens when:
    1. A script is created (triggering domain reload)
    2. Multiple read_console calls are made immediately
    3. The plugin disconnects and reconnects during those calls
    """
    # Setup tools
    from services.tools.read_console import read_console

    call_count = [0]

    async def fake_send_command(*args, **kwargs):
        """Simulate successful command execution."""
        call_count[0] += 1
        return {
            "success": True,
            "message": f"Retrieved {call_count[0]} log entries.",
            "data": ["<b><color=#2EA3FF>MCP-FOR-UNITY</color></b>: Auto-discovered 10 tools"]
        }

    # Patch the async_send_command_with_retry directly
    import services.tools.read_console
    monkeypatch.setattr(
        services.tools.read_console,
        "async_send_command_with_retry",
        fake_send_command
    )

    # Run multiple read_console calls rapidly (simulating the stress test)
    results = []
    for i in range(5):
        result = await read_console(
            ctx=DummyContext(),
            action="get",
            types=["all"],
            count=50,
            format="plain",
            include_stacktrace=False
        )
        results.append(result)

    # All calls should succeed
    assert len(results) == 5
    for i, result in enumerate(results):
        assert result["success"] is True, f"Call {i+1} failed with result: {result}"
        assert "data" in result

    # At least 5 calls should have been made
    assert call_count[0] == 5


@pytest.mark.asyncio
async def test_plugin_hub_respects_unity_instance_preference():
    """Test that _resolve_session_id prefers a specific Unity instance if requested."""
    from transport.plugin_hub import PluginHub, InstanceSelectionRequiredError
    from transport.plugin_registry import PluginRegistry, PluginSession

    # Create a mock registry with two sessions
    mock_registry = AsyncMock(spec=PluginRegistry)

    now = datetime.now()
    session1 = PluginSession(
        session_id="session-1",
        project_name="Project1",
        project_hash="hash1",
        unity_version="2022.3.0f1",
        registered_at=now,
        connected_at=now
    )
    session2 = PluginSession(
        session_id="session-2",
        project_name="Project2",
        project_hash="hash2",
        unity_version="2022.3.0f1",
        registered_at=now,
        connected_at=now
    )

    async def mock_list_sessions(**kwargs):
        return {
            "session-1": session1,
            "session-2": session2
        }

    async def mock_get_session_id_by_hash(project_hash, user_id=None):
        if project_hash == "hash2":
            return "session-2"
        return None

    mock_registry.list_sessions = mock_list_sessions
    mock_registry.get_session_id_by_hash = mock_get_session_id_by_hash

    # Configure PluginHub with our mock while preserving the original state
    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()

    try:
        # Request specific Unity instance
        session_id = await PluginHub._resolve_session_id(unity_instance="hash2")

        # Should return the requested instance
        assert session_id == "session-2"

        # Request default (no specific instance)
        with pytest.raises(InstanceSelectionRequiredError, match="Multiple Unity instances"):
            await PluginHub._resolve_session_id(unity_instance=None)

    finally:
        # Clean up: restore original PluginHub state
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock


# ------------------------------------------------------------------
# Deterministic domain reload detection tests (Issue #657)
# ------------------------------------------------------------------

import time


@pytest.mark.asyncio
async def test_resolve_session_probes_editor_state_after_reconnect():
    """After session reconnects, _resolve_session_id probes get_editor_state
    to confirm the editor is done compiling before returning."""
    from transport.plugin_hub import PluginHub
    from transport.plugin_registry import PluginRegistry, PluginSession

    mock_registry = AsyncMock(spec=PluginRegistry)

    call_count = [0]
    probe_count = [0]

    now = datetime.now()
    session = PluginSession(
        session_id="test-session-probe",
        project_name="TestProject",
        project_hash="probe_hash",
        unity_version="2022.3.0f1",
        registered_at=now,
        connected_at=now,
    )

    async def mock_list_sessions(**kwargs):
        call_count[0] += 1
        if call_count[0] <= 2:
            return {}
        return {"test-session-probe": session}

    async def mock_get_session_id_by_hash(project_hash, user_id=None):
        if call_count[0] <= 2:
            return None
        if project_hash == "probe_hash":
            return "test-session-probe"
        return None

    mock_registry.list_sessions = mock_list_sessions
    mock_registry.get_session_id_by_hash = mock_get_session_id_by_hash

    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    original_known = PluginHub._known_project_hashes.copy()
    original_connections = PluginHub._connections.copy()
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()
    # Pre-seed known hash so we get the longer reload timeout
    PluginHub._known_project_hashes.add("probe_hash")

    # Mock send_command to simulate get_editor_state responses
    async def mock_send_command_impl(cls, session_id, command_type, params):
        if command_type == "get_editor_state":
            probe_count[0] += 1
            if probe_count[0] <= 1:
                # First probe: still compiling
                return {
                    "status": "success",
                    "data": {
                        "compilation": {
                            "is_compiling": True,
                            "is_domain_reload_pending": False,
                        }
                    },
                }
            # Second probe onward: done compiling
            return {
                "status": "success",
                "data": {
                    "compilation": {
                        "is_compiling": False,
                        "is_domain_reload_pending": False,
                    }
                },
            }
        return {"status": "success"}

    original_send = PluginHub.send_command
    PluginHub.send_command = classmethod(mock_send_command_impl)

    try:
        session_id = await PluginHub._resolve_session_id(
            unity_instance="probe_hash"
        )
        assert session_id == "test-session-probe"
        # Should have probed editor state at least twice (once compiling, once ready)
        assert probe_count[0] >= 2
    finally:
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock
        PluginHub._known_project_hashes = original_known
        PluginHub._connections = original_connections
        PluginHub.send_command = original_send


@pytest.mark.asyncio
async def test_resolve_session_proceeds_immediately_when_editor_ready():
    """If the editor reports ready immediately after reconnect, _resolve_session_id
    should not add extra delay."""
    from transport.plugin_hub import PluginHub
    from transport.plugin_registry import PluginRegistry, PluginSession

    mock_registry = AsyncMock(spec=PluginRegistry)

    call_count = [0]

    now = datetime.now()
    session = PluginSession(
        session_id="test-session-fast",
        project_name="TestProject",
        project_hash="fast_hash",
        unity_version="2022.3.0f1",
        registered_at=now,
        connected_at=now,
    )

    async def mock_list_sessions(**kwargs):
        call_count[0] += 1
        if call_count[0] <= 1:
            return {}
        # Single session → auto-select
        return {"test-session-fast": session}

    mock_registry.list_sessions = mock_list_sessions

    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    original_known = PluginHub._known_project_hashes.copy()
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()
    PluginHub._known_project_hashes.add("fast_hash")

    # Editor immediately reports ready
    async def mock_send_command_impl(cls, session_id, command_type, params):
        if command_type == "get_editor_state":
            return {
                "status": "success",
                "data": {
                    "compilation": {
                        "is_compiling": False,
                        "is_domain_reload_pending": False,
                    }
                },
            }
        return {"status": "success"}

    original_send = PluginHub.send_command
    PluginHub.send_command = classmethod(mock_send_command_impl)

    try:
        t0 = time.monotonic()
        # Use unity_instance=None with single session for auto-select
        session_id = await PluginHub._resolve_session_id(
            unity_instance=None
        )
        elapsed = time.monotonic() - t0
        assert session_id == "test-session-fast"
        # Should complete quickly (well under 2s) since editor is ready immediately
        assert elapsed < 2.0
    finally:
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock
        PluginHub._known_project_hashes = original_known
        PluginHub.send_command = original_send


@pytest.mark.asyncio
async def test_resolve_session_fails_fast_without_history(monkeypatch):
    """When no previous session existed for the target hash, _resolve_session_id
    should use the shorter timeout (~5s) instead of the full reload timeout."""
    from transport.plugin_hub import PluginHub, NoUnitySessionError
    from transport.plugin_registry import PluginRegistry

    mock_registry = AsyncMock(spec=PluginRegistry)
    mock_registry.get_session_id_by_hash = AsyncMock(return_value=None)
    mock_registry.list_sessions = AsyncMock(return_value={})

    original_registry = PluginHub._registry
    original_lock = PluginHub._lock
    original_known = PluginHub._known_project_hashes.copy()
    PluginHub._registry = mock_registry
    PluginHub._lock = asyncio.Lock()
    # Ensure no history for target hash
    PluginHub._known_project_hashes.discard("unknown_hash")

    # Don't set env var so adaptive timeout kicks in
    monkeypatch.delenv("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", raising=False)

    try:
        t0 = time.monotonic()
        with pytest.raises(NoUnitySessionError):
            await PluginHub._resolve_session_id(
                unity_instance="unknown_hash"
            )
        elapsed = time.monotonic() - t0
        # Should fail within ~5s (the no-history timeout), not 10s or 20s
        assert elapsed < 7.0
        # Should have waited at least some time
        assert elapsed >= 4.0
    finally:
        PluginHub._registry = original_registry
        PluginHub._lock = original_lock
        PluginHub._known_project_hashes = original_known


@pytest.mark.asyncio
async def test_known_project_hashes_populated_on_register():
    """_known_project_hashes should be populated when a plugin registers."""
    from transport.plugin_hub import PluginHub

    original_known = PluginHub._known_project_hashes.copy()
    try:
        # Simulate what _handle_register does
        test_hash = "test_reg_hash_12345"
        PluginHub._known_project_hashes.add(test_hash)
        assert test_hash in PluginHub._known_project_hashes
    finally:
        PluginHub._known_project_hashes = original_known


@pytest.mark.asyncio
async def test_probe_editor_ready_handles_failure_gracefully():
    """_probe_editor_ready should return False when all probes fail within deadline."""
    from transport.plugin_hub import PluginHub

    async def mock_send_command_impl(cls, session_id, command_type, params):
        raise ConnectionError("Unity disconnected")

    original_send = PluginHub.send_command
    PluginHub.send_command = classmethod(mock_send_command_impl)

    try:
        # Give a very short deadline so it doesn't loop forever
        result = await PluginHub._probe_editor_ready(
            "fake-session", time.monotonic() + 0.5
        )
        # Should return False (deadline exceeded with all failures)
        assert result is False
    finally:
        PluginHub.send_command = original_send


# --- Stdio transport deterministic detection tests ---

def test_stdio_probe_editor_state_sync_success():
    """_probe_editor_state_sync returns compilation dict on success."""
    from transport.legacy.unity_connection import _probe_editor_state_sync

    class MockConn:
        def send_command(self, cmd, params, **kwargs):
            return {
                "data": {
                    "compilation": {
                        "is_compiling": True,
                        "is_domain_reload_pending": False,
                    }
                }
            }

    result = _probe_editor_state_sync(MockConn())
    assert result is not None
    assert result["is_compiling"] is True


def test_stdio_probe_editor_state_sync_failure():
    """_probe_editor_state_sync returns None when probe fails."""
    from transport.legacy.unity_connection import _probe_editor_state_sync

    class MockConn:
        def send_command(self, cmd, params, **kwargs):
            raise ConnectionError("disconnected")

    result = _probe_editor_state_sync(MockConn())
    assert result is None


def test_stdio_is_editor_ready_sync_when_done():
    """_is_editor_ready_sync returns True when not compiling."""
    from transport.legacy.unity_connection import _is_editor_ready_sync

    class MockConn:
        def send_command(self, cmd, params, **kwargs):
            return {
                "data": {
                    "compilation": {
                        "is_compiling": False,
                        "is_domain_reload_pending": False,
                    }
                }
            }

    assert _is_editor_ready_sync(MockConn()) is True


def test_stdio_is_editor_ready_sync_when_compiling():
    """_is_editor_ready_sync returns False when compiling."""
    from transport.legacy.unity_connection import _is_editor_ready_sync

    class MockConn:
        def send_command(self, cmd, params, **kwargs):
            return {
                "data": {
                    "compilation": {
                        "is_compiling": True,
                        "is_domain_reload_pending": False,
                    }
                }
            }

    assert _is_editor_ready_sync(MockConn()) is False


def test_stdio_is_editor_ready_sync_when_reload_pending():
    """_is_editor_ready_sync returns False when domain reload is pending."""
    from transport.legacy.unity_connection import _is_editor_ready_sync

    class MockConn:
        def send_command(self, cmd, params, **kwargs):
            return {
                "data": {
                    "compilation": {
                        "is_compiling": False,
                        "is_domain_reload_pending": True,
                    }
                }
            }

    assert _is_editor_ready_sync(MockConn()) is False
