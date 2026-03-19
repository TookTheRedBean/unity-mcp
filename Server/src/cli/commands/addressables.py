"""Addressables CLI commands for managing Unity Addressable Assets."""

import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success
from cli.utils.connection import run_command, handle_unity_errors
from cli.utils.parsers import parse_json_dict_or_exit


def _send(action: str, extra: dict[str, Any] | None = None) -> dict:
    config = get_config()
    params: dict[str, Any] = {"action": action}
    if extra:
        params.update({k: v for k, v in extra.items() if v is not None})
    result = run_command("manage_addressables", params, config)
    click.echo(format_output(result, config.format))
    return result


@click.group()
def addressables():
    """Addressable Assets – groups, entries, labels & builds."""
    pass


# =============================================================================
# Groups
# =============================================================================

@addressables.command("groups")
@click.option("--page-size", type=int, default=50, help="Results per page.")
@click.option("--page", type=int, default=1, help="Page number (1-based).")
@handle_unity_errors
def groups(page_size: int, page: int):
    """List all addressable groups."""
    _send("group_list", {"page_size": page_size, "page_number": page})


@addressables.command("group-create")
@click.argument("name")
@handle_unity_errors
def group_create(name: str):
    """Create a new addressable group.

    \\b
    Examples:
        unity-mcp addressables group-create MyDLC
        unity-mcp addressables group-create "Remote Assets"
    """
    _send("group_create", {"group": name})


@addressables.command("group-remove")
@click.argument("name")
@handle_unity_errors
def group_remove(name: str):
    """Remove an addressable group (cannot remove default).

    \\b
    Examples:
        unity-mcp addressables group-remove MyDLC
    """
    _send("group_remove", {"group": name})


# =============================================================================
# Entries
# =============================================================================

@addressables.command("add")
@click.argument("path")
@click.option("--group", "-g", default=None, help="Target group (default group if omitted).")
@click.option("--address", "-a", default=None, help="Custom address key.")
@click.option("--labels", "-l", multiple=True, help="Labels to apply (repeatable).")
@handle_unity_errors
def add_entry(path: str, group: Optional[str], address: Optional[str], labels: tuple):
    """Mark an asset as addressable.

    \\b
    PATH can be an asset path (e.g. Assets/Prefabs/Player.prefab) or a GUID.

    \\b
    Examples:
        unity-mcp addressables add Assets/Prefabs/Player.prefab
        unity-mcp addressables add Assets/Prefabs/Player.prefab --group MyDLC --address player
        unity-mcp addressables add Assets/Audio/bgm.wav -l music -l background
    """
    extra: dict[str, Any] = {"path": path}
    if group:
        extra["group"] = group
    if address:
        extra["address"] = address
    if labels:
        extra["labels"] = list(labels)
    _send("entry_add", extra)


@addressables.command("remove")
@click.argument("path")
@handle_unity_errors
def remove_entry(path: str):
    """Remove an asset from addressables.

    \\b
    Examples:
        unity-mcp addressables remove Assets/Prefabs/Player.prefab
    """
    _send("entry_remove", {"path": path})


@addressables.command("set-address")
@click.argument("path")
@click.argument("address")
@handle_unity_errors
def set_address(path: str, address: str):
    """Change an entry's address key.

    \\b
    Examples:
        unity-mcp addressables set-address Assets/Prefabs/Player.prefab player
    """
    _send("entry_set_address", {"path": path, "address": address})


@addressables.command("find")
@click.option("--query", "-q", default=None, help="Search by address or path substring.")
@click.option("--label", "-l", default=None, help="Filter by label.")
@click.option("--group", "-g", default=None, help="Filter by group name.")
@click.option("--page-size", type=int, default=50, help="Results per page.")
@click.option("--page", type=int, default=1, help="Page number (1-based).")
@handle_unity_errors
def find_entries(query: Optional[str], label: Optional[str], group: Optional[str],
                 page_size: int, page: int):
    """Search addressable entries.

    \\b
    Examples:
        unity-mcp addressables find --query player
        unity-mcp addressables find --label music
        unity-mcp addressables find --group MyDLC --page-size 10
    """
    _send("entry_find", {
        "query": query,
        "label": label,
        "group": group,
        "page_size": page_size,
        "page_number": page,
    })


@addressables.command("move")
@click.argument("path")
@click.argument("target_group")
@handle_unity_errors
def move_entry(path: str, target_group: str):
    """Move an entry to a different group.

    \\b
    Examples:
        unity-mcp addressables move Assets/Prefabs/Player.prefab RemoteAssets
    """
    _send("entry_move", {"path": path, "target_group": target_group})


# =============================================================================
# Labels
# =============================================================================

@addressables.command("labels")
@handle_unity_errors
def list_labels():
    """List all defined addressable labels."""
    _send("label_list")


@addressables.command("label-add")
@click.argument("label")
@handle_unity_errors
def label_add(label: str):
    """Add a new label to the global label list.

    \\b
    Examples:
        unity-mcp addressables label-add remote
    """
    _send("label_add", {"label": label})


@addressables.command("label-remove")
@click.argument("label")
@handle_unity_errors
def label_remove(label: str):
    """Remove a label from the global label list.

    \\b
    Examples:
        unity-mcp addressables label-remove remote
    """
    _send("label_remove", {"label": label})


@addressables.command("label-set")
@click.argument("path")
@click.argument("label")
@click.option("--enable/--disable", default=True, help="Enable or disable the label on the entry.")
@handle_unity_errors
def label_set(path: str, label: str, enable: bool):
    """Toggle a label on an addressable entry.

    \\b
    Examples:
        unity-mcp addressables label-set Assets/Prefabs/Player.prefab remote
        unity-mcp addressables label-set Assets/Prefabs/Player.prefab remote --disable
    """
    _send("label_set", {"path": path, "label": label, "enabled": enable})


# =============================================================================
# Profiles
# =============================================================================

@addressables.command("profiles")
@handle_unity_errors
def profiles():
    """List all addressable profiles with their variables."""
    _send("profile_list")


@addressables.command("profile-get")
@click.argument("name")
@handle_unity_errors
def profile_get(name: str):
    """Get a single profile's variables.

    \\b
    Examples:
        unity-mcp addressables profile-get Default
    """
    _send("profile_get", {"profile": name})


@addressables.command("profile-set")
@click.argument("profile")
@click.argument("variable")
@click.argument("value")
@handle_unity_errors
def profile_set(profile: str, variable: str, value: str):
    """Update a variable in a profile.

    \\b
    Examples:
        unity-mcp addressables profile-set Default RemoteBuildPath ServerData/[BuildTarget]
    """
    _send("profile_set", {"profile": profile, "variable": variable, "value": value})


@addressables.command("profile-activate")
@click.argument("name")
@handle_unity_errors
def profile_activate(name: str):
    """Set the active profile.

    \\b
    Examples:
        unity-mcp addressables profile-activate Default
    """
    _send("profile_set_active", {"profile": name})


# =============================================================================
# Build
# =============================================================================

@addressables.command("build")
@handle_unity_errors
def build():
    """Build all addressable content.

    \\b
    This may take a while depending on the project size.

    \\b
    Examples:
        unity-mcp addressables build
    """
    click.echo("Building addressable content...")
    _send("build_content")


@addressables.command("build-update")
@click.option("--content-state-path", default=None, help="Path to content state file (auto-detected if omitted).")
@handle_unity_errors
def build_update(content_state_path: Optional[str]):
    """Incremental content update build.

    \\b
    Builds only changed content since the last full build.
    Auto-detects the content state file unless overridden.

    \\b
    Examples:
        unity-mcp addressables build-update
        unity-mcp addressables build-update --content-state-path path/to/state.bin
    """
    click.echo("Building addressable content update...")
    _send("build_update", {"content_state_path": content_state_path})


@addressables.command("build-clean")
@handle_unity_errors
def build_clean():
    """Clean addressable build artifacts.

    \\b
    Examples:
        unity-mcp addressables build-clean
    """
    _send("build_clean")


# =============================================================================
# Settings
# =============================================================================

@addressables.command("settings")
@handle_unity_errors
def settings():
    """Get global Addressables configuration."""
    _send("get_settings")


@addressables.command("set-setting")
@click.argument("key")
@click.argument("value")
@handle_unity_errors
def set_setting(key: str, value: str):
    """Update a global Addressables setting.

    \\b
    Valid keys: player_version_override, max_concurrent_web_requests,
    unique_bundle_ids, contiguous_bundles, non_recursive_dependency_calculation,
    build_remote_catalog.

    \\b
    Examples:
        unity-mcp addressables set-setting max_concurrent_web_requests 128
        unity-mcp addressables set-setting unique_bundle_ids true
    """
    _send("set_settings", {"key": key, "value": value})


# =============================================================================
# Raw Command (escape hatch)
# =============================================================================

@addressables.command("raw")
@click.argument("action")
@click.option("--params", "-p", default="{}", help="Additional parameters as JSON.")
@handle_unity_errors
def raw(action: str, params: str):
    """Execute any addressables action directly.

    \\b
    Examples:
        unity-mcp addressables raw ping
        unity-mcp addressables raw group_list --params '{"page_size": 10}'
        unity-mcp addressables raw entry_find --params '{"query": "player", "label": "remote"}'
    """
    extra = parse_json_dict_or_exit(params, "params")
    _send(action, extra)
