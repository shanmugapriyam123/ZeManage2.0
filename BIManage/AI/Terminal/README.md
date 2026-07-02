# BIManage.AI.Terminal

ZeManage's AI Terminal and MCP server. Lets users (and AI clients like Claude Desktop) execute Revit operations via natural language and the Model Context Protocol.

## Status

Phase 3a — **foundation in progress**.

## Folder Layout

| Folder | Purpose |
|--------|---------|
| `McpServer/` | C# MCP server hosting both stdio and HTTP+SSE transports |
| `Tools/` | The 13 MCP tools (10 from Sparx fork, 3 ZeManage-specific) |
| `Safety/` | Roslyn AST static analyzer, namespace allowlist, execution timeout |
| `Execution/` | C# script executor, ExternalEventManager, RevitScriptGlobals |
| `Models/` | Shared data models for tool inputs/outputs |

## Tools

### From Sparx Fork (10)
1. `say_hello` — Connection/health check
2. `analyze_model_statistics` — Element/type/view/sheet counts
3. `export_room_data` — Room schedule export
4. `get_material_quantities` — Material takeoff
5. `get_selected_elements` — Selected element IDs + properties
6. `ai_element_filter` — Intelligent element filtering
7. `color_splash` — Color elements by parameter value
8. `get_current_view_info` — Viewport, scale, level info
9. `get_available_family_types` — Loadable families in project
10. `send_code_to_revit` — Execute AI-generated C# (HARDENED)

### ZeManage-Specific (3 — net new)
11. `query_audit_log` — Search ZeManage's audit trail
12. `check_protection_rules` — List active protection rules
13. `get_health_alerts` — Current model health alerts

## Security Model — Tier 1 Only (v1)

| Tier | Allowed | Approval | Status |
|------|---------|----------|--------|
| Tier 1 | Read-only queries | None | **Enabled** |
| Tier 2 | Property writes within transactions | Admin OTP | Disabled |
| Tier 3 | Element creation/deletion | Admin OTP + protection rule | Disabled |

The static analyzer rejects any code matching Tier 2 or Tier 3 patterns at parse time.

## Attribution

Code in this folder includes adaptations from the [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) MIT-licensed project. See `THIRD_PARTY_NOTICES.md` for the full notice.
