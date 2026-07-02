using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.ExportRoomData;

/// <summary>
/// External event handler for the <c>export_room_data</c> MCP tool.
/// Iterates all rooms in the active document and projects each into a <see cref="RoomDataModel"/>.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class ExportRoomDataEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);
    private bool _includeUnplacedRooms;
    private bool _includeNotEnclosedRooms;

    public ExportRoomDataResult? ResultInfo { get; private set; }

    public void SetParameters(bool includeUnplacedRooms, bool includeNotEnclosedRooms)
    {
        _includeUnplacedRooms = includeUnplacedRooms;
        _includeNotEnclosedRooms = includeNotEnclosedRooms;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                ResultInfo = new ExportRoomDataResult { Success = false, Message = "No active document" };
                return;
            }

            var rooms = new List<RoomDataModel>();
            double totalArea = 0;

            var roomCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>();

            foreach (var room in roomCollector)
            {
                // Area==0 indicates either unplaced OR not-enclosed. Skip both classes
                // unless the caller explicitly opted in to either.
                if (room.Area == 0 && !_includeUnplacedRooms && !_includeNotEnclosedRooms)
                    continue;

                var phaseElementId = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
                var phaseName = phaseElementId != null ? doc.GetElement(phaseElementId)?.Name : null;

                var roomData = new RoomDataModel
                {
#if REVIT2024_OR_GREATER
                    Id = room.Id.Value,
#else
                    Id = room.Id.IntegerValue,
#endif
                    UniqueId = room.UniqueId,
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                    Number = room.Number ?? "",
                    Level = room.Level?.Name ?? "No Level",
                    Area = room.Area,
                    Volume = room.Volume,
                    Perimeter = room.Perimeter,
                    UnboundedHeight = room.UnboundedHeight,
                    Department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "",
                    Comments = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                    Phase = phaseName ?? "",
                    Occupancy = room.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsString() ?? ""
                };

                rooms.Add(roomData);
                totalArea += room.Area;
            }

            ResultInfo = new ExportRoomDataResult
            {
                TotalRooms = rooms.Count,
                TotalArea = totalArea,
                Rooms = rooms,
                Success = true,
                Message = $"Successfully exported {rooms.Count} rooms"
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new ExportRoomDataResult
            {
                Success = false,
                Message = $"Error exporting room data: {ex.Message}"
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Export Room Data";
}
