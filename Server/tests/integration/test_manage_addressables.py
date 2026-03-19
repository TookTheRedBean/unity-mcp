import asyncio

from .test_helpers import DummyContext
import services.tools.manage_addressables as mod


def test_unknown_action(monkeypatch):
    """Unknown action returns error without calling Unity."""
    called = False

    async def fake_send(cmd, params, **kwargs):
        nonlocal called
        called = True
        return {"success": True}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="not_a_real_action")
    )

    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert not called


def test_ping_sends_correct_params(monkeypatch):
    """ping action sends correct params to Unity."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {"success": True, "data": {"available": True}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="ping")
    )

    assert result["success"] is True
    assert captured["cmd"] == "manage_addressables"
    assert captured["params"]["action"] == "ping"


def test_group_create_sends_group_param(monkeypatch):
    """group_create passes group name correctly."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"name": "MyGroup"}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="group_create", group="MyGroup")
    )

    assert result["success"] is True
    assert captured["params"]["group"] == "MyGroup"


def test_entry_add_with_all_params(monkeypatch):
    """entry_add forwards path, group, address, and labels."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="entry_add",
            path="Assets/Prefabs/Player.prefab",
            group="MyGroup",
            address="player",
            labels=["remote", "dlc"],
        )
    )

    assert result["success"] is True
    p = captured["params"]
    assert p["action"] == "entry_add"
    assert p["path"] == "Assets/Prefabs/Player.prefab"
    assert p["group"] == "MyGroup"
    assert p["address"] == "player"
    assert p["labels"] == ["remote", "dlc"]


def test_entry_find_pagination_coercion(monkeypatch):
    """Pagination params are coerced to int."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="entry_find",
            query="player",
            page_size="25",
            page_number="2",
        )
    )

    assert result["success"] is True
    assert captured["params"]["page_size"] == 25
    assert captured["params"]["page_number"] == 2


def test_label_set_enabled_default(monkeypatch):
    """label_set passes enabled flag when provided."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="label_set",
            path="Assets/Prefabs/Player.prefab",
            label="remote",
            enabled=False,
        )
    )

    assert result["success"] is True
    assert captured["params"]["enabled"] is False


def test_entry_move_params(monkeypatch):
    """entry_move forwards path and target_group."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="entry_move",
            path="Assets/Prefabs/Player.prefab",
            target_group="RemoteAssets",
        )
    )

    assert result["success"] is True
    p = captured["params"]
    assert p["action"] == "entry_move"
    assert p["path"] == "Assets/Prefabs/Player.prefab"
    assert p["target_group"] == "RemoteAssets"


def test_none_params_excluded(monkeypatch):
    """Optional params that are None are not sent to Unity."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="label_list")
    )

    assert result["success"] is True
    # Only action should be present — no None keys
    assert captured["params"] == {"action": "label_list"}


def test_build_content_sends_action(monkeypatch):
    """build_content sends the correct action."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"duration": 12.5}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="build_content")
    )

    assert result["success"] is True
    assert captured["params"]["action"] == "build_content"


# =============================================================================
# Phase 2: Profiles, Builds & Settings
# =============================================================================


def test_profile_list_sends_action(monkeypatch):
    """profile_list sends action with no extra params."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"profiles": []}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="profile_list")
    )

    assert result["success"] is True
    assert captured["params"] == {"action": "profile_list"}


def test_profile_get_sends_profile_param(monkeypatch):
    """profile_get forwards profile name."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"name": "Default"}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="profile_get", profile="Default")
    )

    assert result["success"] is True
    assert captured["params"]["action"] == "profile_get"
    assert captured["params"]["profile"] == "Default"


def test_profile_set_sends_all_params(monkeypatch):
    """profile_set forwards profile, variable, and value."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="profile_set",
            profile="Default",
            variable="RemoteBuildPath",
            value="ServerData/[BuildTarget]",
        )
    )

    assert result["success"] is True
    p = captured["params"]
    assert p["action"] == "profile_set"
    assert p["profile"] == "Default"
    assert p["variable"] == "RemoteBuildPath"
    assert p["value"] == "ServerData/[BuildTarget]"


def test_profile_set_active_sends_profile_param(monkeypatch):
    """profile_set_active forwards profile name."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="profile_set_active", profile="Default")
    )

    assert result["success"] is True
    assert captured["params"]["action"] == "profile_set_active"
    assert captured["params"]["profile"] == "Default"


def test_build_update_sends_action(monkeypatch):
    """build_update sends action, content_state_path is optional."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"duration": 5.0}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    # Without content_state_path
    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="build_update")
    )

    assert result["success"] is True
    assert captured["params"] == {"action": "build_update"}

    # With content_state_path
    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="build_update",
            content_state_path="Library/state.bin",
        )
    )

    assert result["success"] is True
    assert captured["params"]["content_state_path"] == "Library/state.bin"


def test_build_clean_sends_action(monkeypatch):
    """build_clean sends action with no extra params."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="build_clean")
    )

    assert result["success"] is True
    assert captured["params"] == {"action": "build_clean"}


def test_get_settings_sends_action(monkeypatch):
    """get_settings sends action with no extra params."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"active_profile": "Default"}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(ctx=DummyContext(), action="get_settings")
    )

    assert result["success"] is True
    assert captured["params"] == {"action": "get_settings"}


def test_set_settings_sends_key_value(monkeypatch):
    """set_settings forwards key and value."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    result = asyncio.run(
        mod.manage_addressables(
            ctx=DummyContext(),
            action="set_settings",
            key="max_concurrent_web_requests",
            value="128",
        )
    )

    assert result["success"] is True
    p = captured["params"]
    assert p["action"] == "set_settings"
    assert p["key"] == "max_concurrent_web_requests"
    assert p["value"] == "128"
