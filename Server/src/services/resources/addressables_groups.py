from typing import Any

from fastmcp import Context

from models import MCPResponse
from models.unity_response import parse_resource_response
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


class AddressablesGroupsResponse(MCPResponse):
    """Summary of addressable groups with entry counts."""
    data: dict[str, Any] = {}


@mcp_for_unity_resource(
    uri="mcpforunity://addressables/groups",
    name="addressables_groups",
    description=(
        "Addressable asset groups summary: group names, entry counts, and schema types. "
        "Read this to understand the current Addressables layout before making changes.\n\n"
        "URI: mcpforunity://addressables/groups"
    ),
)
async def get_addressables_groups(ctx: Context) -> AddressablesGroupsResponse | MCPResponse:
    """Get addressable groups summary."""
    unity_instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_addressables",
        {"action": "group_list", "page_size": 100},
    )
    return parse_resource_response(response, AddressablesGroupsResponse)
