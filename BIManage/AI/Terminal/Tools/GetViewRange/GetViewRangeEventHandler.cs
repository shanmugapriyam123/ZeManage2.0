using System;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.Tools.GetViewRange;

/// <summary>
/// Handler for <c>get_view_range</c>. Runs on Revit's UI thread because PlanViewRange
/// and Level lookups touch the document model.
/// </summary>
internal sealed class GetViewRangeEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    private int? _viewId;

    public ViewRangeResult? Result { get; private set; }

    public void SetParameters(int? viewId)
    {
        _viewId = viewId;
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
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null)
            {
                Result = new ViewRangeResult { Success = false, ErrorMessage = "No active document." };
                return;
            }
            var doc = uiDoc.Document;

            // Resolve the view: explicit id if supplied, otherwise active view.
            View? view = null;
            if (_viewId.HasValue)
            {
#if REVIT2024_OR_GREATER
                view = doc.GetElement(new ElementId((long)_viewId.Value)) as View;
#else
                view = doc.GetElement(new ElementId(_viewId.Value)) as View;
#endif
                if (view == null)
                {
                    Result = new ViewRangeResult
                    {
                        Success = false,
                        ErrorMessage = $"No view found with ElementId {_viewId.Value}."
                    };
                    return;
                }
            }
            else
            {
                view = uiDoc.ActiveView;
            }

            if (view is not ViewPlan plan)
            {
                Result = new ViewRangeResult
                {
                    Success = false,
                    ErrorMessage = $"View '{view?.Name ?? "(unknown)"}' is a {view?.ViewType.ToString() ?? "non-plan"} view. " +
                                   "View ranges only apply to plan views (FloorPlan / CeilingPlan / AreaPlan / EngineeringPlan)."
                };
                return;
            }

            var range = plan.GetViewRange();
            Result = new ViewRangeResult
            {
                Success = true,
                ViewId = GetIntId(view.Id),
                ViewName = view.Name,
                ViewType = view.ViewType.ToString(),
                TopClipPlane     = ReadPlane(doc, range, PlanViewPlane.TopClipPlane),
                CutPlane         = ReadPlane(doc, range, PlanViewPlane.CutPlane),
                BottomClipPlane  = ReadPlane(doc, range, PlanViewPlane.BottomClipPlane),
                ViewDepthPlane   = ReadPlane(doc, range, PlanViewPlane.ViewDepthPlane),
                UnderlayBottom   = ReadPlane(doc, range, PlanViewPlane.UnderlayBottom),
                UnderlayTop      = TryReadOptionalPlane(doc, range, "UnderlayTop")
            };
        }
        catch (Exception ex)
        {
            Result = new ViewRangeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private static long GetIntId(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    // 1 foot = 304.8 mm exactly (international foot, which is what Revit's API uses
    // internally for all length values regardless of project units).
    private const double MmPerFoot = 304.8;

    private static ViewRangePlane ReadPlane(Document doc, PlanViewRange range, PlanViewPlane plane)
    {
        var levelId = range.GetLevelId(plane);
        var offset = range.GetOffset(plane);
        var level = levelId == ElementId.InvalidElementId ? null : doc.GetElement(levelId) as Level;
        double? effectiveFeet = level != null ? level.Elevation + offset : (double?)null;
        double? levelElevationMm = level != null ? Math.Round(level.Elevation * MmPerFoot, 1) : (double?)null;
        double offsetMm = Math.Round(offset * MmPerFoot, 1);
        double? effectiveMm = effectiveFeet.HasValue ? Math.Round(effectiveFeet.Value * MmPerFoot, 1) : (double?)null;

        return new ViewRangePlane
        {
            PlaneName = plane.ToString(),
            LevelId = levelId == ElementId.InvalidElementId ? (long?)null : GetIntId(levelId),
            LevelName = level?.Name,
            LevelElevationFeet = level?.Elevation,
            LevelElevationMm = levelElevationMm,
            OffsetFeet = offset,
            OffsetMm = offsetMm,
            EffectiveHeightFeet = effectiveFeet,
            EffectiveHeightMm = effectiveMm,
            // Pre-formatted display strings the AI can quote verbatim. This is the most
            // important field for accuracy — GPT-4o-mini hallucinates unit conversions
            // (observed in 2026-05-26 testing: same 170.96 ft input produced "1785 mm",
            // "1768.70 mm", and "5200 mm" responses within an hour). When the tool ships
            // the canonical string, the model just quotes it.
            DisplayString = effectiveFeet.HasValue
                ? $"{effectiveMm:F0} mm ({effectiveFeet:F2} ft) — level '{level?.Name}' + offset {offsetMm:F0} mm"
                : "(no level bound)"
        };
    }

    // UnderlayTop only exists in newer SDKs. Catch and return null on older Revit versions
    // rather than #if-fencing — same binary works across Revit 2021–2027 then.
    private static ViewRangePlane? TryReadOptionalPlane(Document doc, PlanViewRange range, string planeName)
    {
        try
        {
            if (Enum.TryParse<PlanViewPlane>(planeName, out var plane))
                return ReadPlane(doc, range, plane);
        }
        catch { /* enum value not available on this Revit version */ }
        return null;
    }

    public string GetName() => "ZeManage Get View Range";
}

public sealed class ViewRangeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long ViewId { get; set; }
    public string? ViewName { get; set; }
    public string? ViewType { get; set; }
    public ViewRangePlane? TopClipPlane { get; set; }
    public ViewRangePlane? CutPlane { get; set; }
    public ViewRangePlane? BottomClipPlane { get; set; }
    public ViewRangePlane? ViewDepthPlane { get; set; }
    public ViewRangePlane? UnderlayBottom { get; set; }
    public ViewRangePlane? UnderlayTop { get; set; }
}

public sealed class ViewRangePlane
{
    public string PlaneName { get; set; } = "";
    public long? LevelId { get; set; }
    public string? LevelName { get; set; }
    public double? LevelElevationFeet { get; set; }
    public double? LevelElevationMm { get; set; }
    public double OffsetFeet { get; set; }
    public double OffsetMm { get; set; }
    /// <summary>Level elevation + offset — what the plane is at, in feet. Null when no level is bound.</summary>
    public double? EffectiveHeightFeet { get; set; }
    /// <summary>Same as <see cref="EffectiveHeightFeet"/> but in millimetres. Pre-converted
    /// so the AI never has to multiply by 304.8 itself — that arithmetic has been observed
    /// to hallucinate (different answers from identical inputs).</summary>
    public double? EffectiveHeightMm { get; set; }
    /// <summary>Canonical pre-formatted line the AI should quote verbatim instead of
    /// re-stating numbers. Eliminates unit-conversion fabrication.</summary>
    public string DisplayString { get; set; } = "";
}
