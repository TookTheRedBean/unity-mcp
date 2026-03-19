from typing import Annotated, Any, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

ALL_ACTIONS = [
    "ping",
    "group_list", "group_create", "group_remove",
    "entry_add", "entry_remove", "entry_set_address", "entry_find", "entry_move",
    "label_list", "label_add", "label_remove", "label_set",
    "profile_list", "profile_get", "profile_set", "profile_set_active",
    "build_content", "build_update", "build_clean",
    "get_settings", "set_settings",
]


async def _send_addressables_command(
    ctx: Context,
    params_dict: dict[str, Any],
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "manage_addressables", params_dict
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}


@mcp_for_unity_tool(
    group="addressables",
    description=(
        "Manage Unity Addressable Assets: groups, entries, labels, profiles, builds & settings.\n"
        "Requires com.unity.addressables package to be installed in the Unity project.\n\n"
        "GROUPS:\n"
        "- group_list: List all addressable groups (paged)\n"
        "- group_create: Create a new group (group required)\n"
        "- group_remove: Remove a group (group required, cannot remove default)\n\n"
        "ENTRIES:\n"
        "- entry_add: Mark an asset as addressable (path required, group/address/labels optional)\n"
        "- entry_remove: Remove an asset from addressables (path required)\n"
        "- entry_set_address: Change an entry's address key (path + address required)\n"
        "- entry_find: Search entries by address/path, label, or group (paged)\n"
        "- entry_move: Move an entry to a different group (path + target_group required)\n\n"
        "LABELS:\n"
        "- label_list: List all defined labels\n"
        "- label_add: Add a new label to the global list (label required)\n"
        "- label_remove: Remove a label from the global list (label required)\n"
        "- label_set: Toggle a label on an entry (path + label required, enabled defaults to true)\n\n"
        "PROFILES:\n"
        "- profile_list: List all profiles with their variables\n"
        "- profile_get: Get a single profile's variables (profile required)\n"
        "- profile_set: Update a variable in a profile (profile + variable + value required)\n"
        "- profile_set_active: Switch the active profile (profile required)\n\n"
        "BUILD:\n"
        "- build_content: Full build of all addressable content (may take a while)\n"
        "- build_update: Incremental content update build (content_state_path optional)\n"
        "- build_clean: Clean addressable build artifacts\n\n"
        "SETTINGS:\n"
        "- get_settings: Read global Addressables configuration\n"
        "- set_settings: Update a setting by key (key + value required)\n\n"
        "UTILITY:\n"
        "- ping: Check if Addressables is available and return version info"
    ),
    annotations=ToolAnnotations(
        title="Manage Addressables",
        destructiveHint=True,
        readOnlyHint=False,
    ),
)
async def manage_addressables(
    ctx: Context,
    action: Annotated[str, "The addressables action to perform."],
    path: Annotated[Optional[str], "Asset path or GUID for entry operations."] = None,
    group: Annotated[Optional[str], "Group name for group/entry operations."] = None,
    address: Annotated[Optional[str], "Addressable address key for entry_add/entry_set_address."] = None,
    label: Annotated[Optional[str], "Label name for label operations."] = None,
    labels: Annotated[Optional[list[str]], "List of labels to apply in entry_add."] = None,
    enabled: Annotated[Optional[bool], "Enable/disable flag for label_set (default true)."] = None,
    query: Annotated[Optional[str], "Search query for entry_find (matches address or path)."] = None,
    target_group: Annotated[Optional[str], "Target group name for entry_move."] = None,
    profile: Annotated[Optional[str], "Profile name for profile operations."] = None,
    variable: Annotated[Optional[str], "Profile variable name for profile_set."] = None,
    value: Annotated[Optional[str], "Value for profile_set or set_settings."] = None,
    key: Annotated[Optional[str], "Settings key for set_settings."] = None,
    content_state_path: Annotated[Optional[str], "Content state file path for build_update."] = None,
    page_size: Annotated[Optional[int], "Results per page for paged actions."] = None,
    page_number: Annotated[Optional[int], "Page number (1-based) for paged actions."] = None,
) -> dict[str, Any]:
    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid actions: {', '.join(ALL_ACTIONS)}",
        }

    params_dict: dict[str, Any] = {"action": action_lower}

    # Map Python snake_case params to what C# expects (ToolParams handles both cases)
    param_map = {
        "path": path,
        "group": group,
        "address": address,
        "label": label,
        "labels": labels,
        "enabled": enabled,
        "query": query,
        "target_group": target_group,
        "profile": profile,
        "variable": variable,
        "value": value,
        "key": key,
        "content_state_path": content_state_path,
    }
    for key, val in param_map.items():
        if val is not None:
            params_dict[key] = val

    # Coerce pagination to int
    if page_size is not None:
        params_dict["page_size"] = int(page_size)
    if page_number is not None:
        params_dict["page_number"] = int(page_number)

    return await _send_addressables_command(ctx, params_dict)
