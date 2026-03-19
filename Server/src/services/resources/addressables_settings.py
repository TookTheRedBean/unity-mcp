from typing import Any

from fastmcp import Context

from models import MCPResponse
from models.unity_response import parse_resource_response
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


class AddressablesSettingsResponse(MCPResponse):
    """Addressables global settings snapshot."""
    data: dict[str, Any] = {}


@mcp_for_unity_resource(
    uri="mcpforunity://addressables/settings",
    name="addressables_settings",
    description=(
        "Addressables global settings: active profile, build config, group/label/profile counts. "
        "Read this for a quick overview of the Addressables configuration.\n\n"
        "URI: mcpforunity://addressables/settings"
    ),
)
async def get_addressables_settings(ctx: Context) -> AddressablesSettingsResponse | MCPResponse:
    """Get addressables global settings snapshot."""
    unity_instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_addressables",
        {"action": "get_settings"},
    )
    return parse_resource_response(response, AddressablesSettingsResponse)
