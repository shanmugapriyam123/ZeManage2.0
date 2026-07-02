using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.ExportRoomData;

/// <summary>
/// MCP tool: <c>export_room_data</c> — exports all rooms in the active document with
/// area, volume, perimeter, level, department, occupancy, phase, and comments.
/// </summary>
/// <remarks>
/// Optional parameters (all default false):
/// <list type="bullet">
///   <item><c>includeUnplacedRooms</c>: include rooms not placed in the model</item>
///   <item><c>includeNotEnclosedRooms</c>: include rooms whose bounding walls are missing</item>
/// </list>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class ExportRoomDataCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "export_room_data";

    public override string Description =>
        "Exports every room in the active document with name, number, level, area (sq.ft), " +
        "volume (cu.ft), perimeter (ft), department, occupancy, phase, and comments. Use for " +
        "'list my rooms', 'what's the area of each room', 'export the room schedule', etc.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["includeUnplacedRooms"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "Include rooms defined in the project but not placed in any view. Default false."
            },
            ["includeNotEnclosedRooms"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "Include rooms whose bounding walls are missing (area = 0). Default false."
            }
        }
    };

    public ExportRoomDataCommand(UIApplication uiApp)
        : base(new ExportRoomDataEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (ExportRoomDataEventHandler)Handler;
                handler.SetParameters(
                    includeUnplacedRooms: TryReadBool(parameters, "includeUnplacedRooms") ?? false,
                    includeNotEnclosedRooms: TryReadBool(parameters, "includeNotEnclosedRooms") ?? false);

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(60_000))
                {
                    throw new TimeoutException("export_room_data timed out after 60 seconds");
                }

                return handler.ResultInfo
                    ?? throw new InvalidOperationException("Room data export did not produce a result");
            }
            catch (Exception ex)
            {
                throw new Exception($"export_room_data failed: {ex.Message}", ex);
            }
        }
    }

    private static bool? TryReadBool(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
