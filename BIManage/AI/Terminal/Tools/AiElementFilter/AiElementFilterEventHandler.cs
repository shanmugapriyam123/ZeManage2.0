using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.AiElementFilter;

/// <summary>
/// External event handler for the <c>ai_element_filter</c> MCP tool.
/// Builds a FilteredElementCollector from the supplied <see cref="FilterSettings"/>
/// and projects matches into <see cref="ElementInfo"/>.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class AiElementFilterEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);
    private FilterSettings _settings = new();

    public List<ElementInfo> Result { get; private set; } = new();
    public string? ResultMessage { get; private set; }
    public bool ResultSuccess { get; private set; }

    /// <summary>
    /// Total matching elements before any <see cref="FilterSettings.MaxElements"/> cap was applied.
    /// When equal to <c>Result.Count</c>, no truncation happened. When greater, the LLM should
    /// quote this number (not <c>Result.Count</c>) when reporting totals to the user.
    /// </summary>
    public int TotalMatched { get; private set; }

    /// <summary>True when <see cref="Result"/> has fewer entries than <see cref="TotalMatched"/>.</summary>
    public bool IsTruncated => TotalMatched > Result.Count;

    /// <summary>
    /// Diagnostic — number of Revit links in the host document at query time. Set on every
    /// run so the LLM can recognise federated/coordination models where the host doc has
    /// few native elements but many linked ones. Critical context for "0 walls" answers.
    /// </summary>
    public int HostLinkedRevitCount { get; private set; }

    /// <summary>Diagnostic — name of the host document (e.g. central file title).</summary>
    public string? HostDocumentTitle { get; private set; }

    /// <summary>Diagnostic — total non-type element count in the host document.</summary>
    public int HostTotalElementCount { get; private set; }

    public void SetParameters(FilterSettings settings)
    {
        _settings = settings;
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
                Result = new();
                ResultSuccess = false;
                ResultMessage = "No active document";
                return;
            }

            // Capture host-document context up-front. Even a successful "found 0" result
            // becomes self-explanatory when the LLM can see "host has 7 linked Revit models" —
            // it'll know to suggest the user check the link, not redo VG settings.
            HostDocumentTitle = doc.Title;
            HostTotalElementCount = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();
            HostLinkedRevitCount = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RvtLinks)
                .WhereElementIsNotElementType()
                .GetElementCount();

            if (!_settings.Validate(out var validationError))
            {
                Result = new();
                ResultSuccess = false;
                ResultMessage = validationError;
                return;
            }

            var collected = new List<Element>();

            if (_settings.IncludeInstances)
                collected.AddRange(CollectByKind(doc, _settings, isElementType: false));
            if (_settings.IncludeTypes)
                collected.AddRange(CollectByKind(doc, _settings, isElementType: true));

            TotalMatched = collected.Count;

            if (_settings.MaxElements > 0 && collected.Count > _settings.MaxElements)
            {
                collected = collected.Take(_settings.MaxElements).ToList();
            }

            Result = collected.Select(e => new ElementInfo
            {
                Id = e.Id.GetValue(),
                UniqueId = e.UniqueId,
                Name = e.Name,
                Category = e.Category?.Name,
                Properties = ExtractKeyProperties(e)
            }).ToList();

            ResultSuccess = true;

            // Build a context-rich message. When count==0 in a model with linked Revit files,
            // call that out explicitly — this is the single biggest source of "but I can SEE
            // them!" confusion in federated/MEP/coordination models.
            if (TotalMatched == 0 && HostLinkedRevitCount > 0)
            {
                ResultMessage =
                    $"Found 0 matching elements in the HOST document '{HostDocumentTitle}'. " +
                    $"Note: this document has {HostLinkedRevitCount} linked Revit model(s) and " +
                    $"{HostTotalElementCount} native elements total. Elements visible in your " +
                    $"view may live inside the linked models (this tool searches only the host " +
                    $"document). Walls in MEP/coordination models, for example, typically " +
                    $"belong to a linked architectural model rather than the host.";
            }
            else if (TotalMatched == 0)
            {
                ResultMessage =
                    $"Found 0 matching elements in '{HostDocumentTitle}' " +
                    $"(host has {HostTotalElementCount} native elements, no linked Revit models).";
            }
            else if (IsTruncated)
            {
                ResultMessage =
                    $"Found {TotalMatched} matching element(s) in the model. " +
                    $"Returning the first {Result.Count} in the elements list — when reporting " +
                    $"the count to the user, quote totalMatched ({TotalMatched}), not the " +
                    $"length of the elements array.";
            }
            else
            {
                ResultMessage = $"Found {TotalMatched} element(s).";
            }
        }
        catch (Exception ex)
        {
            Result = new();
            ResultSuccess = false;
            ResultMessage = $"Filter failed: {ex.Message}";
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage AI Element Filter";

    private static IEnumerable<Element> CollectByKind(Document doc, FilterSettings settings, bool isElementType)
    {
        FilteredElementCollector collector;

        if (!isElementType && settings.FilterVisibleInCurrentView && doc.ActiveView != null)
        {
            collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
        }
        else
        {
            collector = new FilteredElementCollector(doc);
        }

        collector = isElementType ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();

        var filters = new List<ElementFilter>();

        // Category filter — accepts BuiltInCategory enum names (e.g. "OST_Walls").
        // When the supplied name doesn't exist, suggest similarly-spelled valid ones so
        // the LLM can self-correct on the next round. This single-handedly fixes the
        // "GPT guesses 'OST_Ducts' but the real name is 'OST_DuctCurves'" failure mode.
        if (!string.IsNullOrWhiteSpace(settings.FilterCategory))
        {
            if (Enum.TryParse<BuiltInCategory>(settings.FilterCategory, ignoreCase: true, out var category))
            {
                filters.Add(new ElementCategoryFilter(category));
            }
            else
            {
                var suggestions = SuggestSimilarCategories(settings.FilterCategory!);
                var suggestionText = suggestions.Count > 0
                    ? $" Did you mean: {string.Join(", ", suggestions)}?"
                    : "";
                throw new ArgumentException(
                    $"'{settings.FilterCategory}' is not a valid BuiltInCategory enum name." + suggestionText);
            }
        }

        // Element class filter — try direct, then with Autodesk.Revit.DB namespace
        if (!string.IsNullOrWhiteSpace(settings.FilterElementType))
        {
            Type? elementType = null;
            string[] candidates =
            {
                settings.FilterElementType!,
                $"Autodesk.Revit.DB.{settings.FilterElementType}, RevitAPI",
                $"{settings.FilterElementType}, RevitAPI"
            };
            foreach (var name in candidates)
            {
                elementType = Type.GetType(name);
                if (elementType != null) break;
            }

            if (elementType == null)
            {
                throw new ArgumentException(
                    $"Element type '{settings.FilterElementType}' not found in the Revit API");
            }

            filters.Add(new ElementClassFilter(elementType));
        }

        if (filters.Count > 0)
        {
            ElementFilter combined = filters.Count == 1
                ? filters[0]
                : new LogicalAndFilter(filters);
            collector = collector.WherePasses(combined);
        }

        return collector.ToElements();
    }

    /// <summary>
    /// Returns up to 5 valid <see cref="BuiltInCategory"/> enum names that are similar to
    /// the supplied (invalid) name. Powers self-correction when the LLM guesses a category
    /// name that doesn't exist (e.g. "OST_Ducts" → suggests "OST_DuctCurves, OST_DuctFitting").
    /// </summary>
    /// <remarks>
    /// Two-pass match: first finds names that contain the user's substring (after stripping
    /// the OST_ prefix and trailing 's' for plural→singular), then fuzzy-matches by shared
    /// character n-grams. Cheap enough to run on every miss (~150 enum members to scan).
    /// </remarks>
    private static List<string> SuggestSimilarCategories(string requested)
    {
        var allNames = Enum.GetNames(typeof(BuiltInCategory));

        // Normalise the requested name for matching: strip OST_ prefix, lowercase, depluralise.
        var needle = StripOstPrefix(requested).ToLowerInvariant().TrimEnd('s');

        if (needle.Length < 3) return new List<string>();

        // Pass 1: exact substring matches (case-insensitive, after OST_ strip).
        var substringMatches = allNames
            .Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(n => n.Length) // shorter names first — usually the canonical category
            .Take(5)
            .ToList();

        if (substringMatches.Count > 0) return substringMatches;

        // Pass 2: fuzzy via shared character bigrams. Cheap and good enough for typo
        // recovery without pulling in a Levenshtein dependency.
        return allNames
            .Select(n => new
            {
                Name = n,
                Score = SharedBigramCount(needle, StripOstPrefix(n).ToLowerInvariant())
            })
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>
    /// Removes a leading "OST_" prefix in a case-insensitive way.
    /// (string.Replace(string, string, StringComparison) is .NET Core+ only — net48 needs this manual version.)
    /// </summary>
    private static string StripOstPrefix(string s)
        => s.Length >= 4 && s.StartsWith("ost_", StringComparison.OrdinalIgnoreCase)
            ? s.Substring(4)
            : s;

    private static int SharedBigramCount(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return 0;
        var aBigrams = new HashSet<string>();
        for (int i = 0; i < a.Length - 1; i++) aBigrams.Add(a.Substring(i, 2));
        int shared = 0;
        for (int i = 0; i < b.Length - 1; i++)
            if (aBigrams.Contains(b.Substring(i, 2))) shared++;
        return shared;
    }

    /// <summary>
    /// Returns a small curated set of element parameters that are useful to the LLM
    /// without blowing up the response size. Covers identity (Family, Type, Mark),
    /// position (Level), free-text (Comments), and one or two key dimensional values.
    /// </summary>
    /// <remarks>
    /// Each match adds ~30-80 chars to the JSON response per element. With our default
    /// MaxElements cap of 100, that means an extra ~3-8 KB per response — well within
    /// OpenAI's token budget and small enough that GPT can usefully scan all of them.
    /// </remarks>
    private static Dictionary<string, string> ExtractKeyProperties(Element element)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        TryAddBuiltIn(props, element, BuiltInParameter.ELEM_FAMILY_PARAM, "Family");
        TryAddBuiltIn(props, element, BuiltInParameter.ELEM_TYPE_PARAM, "Type");
        TryAddBuiltIn(props, element, BuiltInParameter.ALL_MODEL_MARK, "Mark");
        TryAddBuiltIn(props, element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments");

        // Level — different element kinds expose it through different parameters, so try a few.
        if (!TryAddBuiltIn(props, element, BuiltInParameter.FAMILY_LEVEL_PARAM, "Level"))
        {
            if (!TryAddBuiltIn(props, element, BuiltInParameter.SCHEDULE_LEVEL_PARAM, "Level"))
            {
                TryAddBuiltIn(props, element, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, "Level");
            }
        }

        // One or two dimensional values per category. Resist the urge to dump every
        // parameter — that would 10x the response size and rarely help the LLM.
        var bic = (BuiltInCategory)(element.Category?.Id.GetIntValue() ?? 0);
        switch (bic)
        {
            case BuiltInCategory.OST_Walls:
                TryAddBuiltIn(props, element, BuiltInParameter.WALL_USER_HEIGHT_PARAM, "UnconnectedHeight");
                break;
            case BuiltInCategory.OST_Doors:
            case BuiltInCategory.OST_Windows:
                TryAddBuiltIn(props, element, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width");
                TryAddBuiltIn(props, element, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height");
                break;
            case BuiltInCategory.OST_Rooms:
                TryAddBuiltIn(props, element, BuiltInParameter.ROOM_AREA, "Area");
                TryAddBuiltIn(props, element, BuiltInParameter.ROOM_NUMBER, "Number");
                break;
            case BuiltInCategory.OST_DuctCurves:
            case BuiltInCategory.OST_PipeCurves:
                TryAddBuiltIn(props, element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, "Diameter");
                TryAddBuiltIn(props, element, BuiltInParameter.CURVE_ELEM_LENGTH, "Length");
                break;
        }

        return props;
    }

    /// <summary>
    /// Reads a built-in parameter as a display string and adds it to the dictionary
    /// if non-empty. Returns true when something was added — used by the Level lookup
    /// to fall through to the next candidate parameter on miss.
    /// </summary>
    private static bool TryAddBuiltIn(
        Dictionary<string, string> props, Element element, BuiltInParameter bip, string key)
    {
        var p = element.get_Parameter(bip);
        if (p == null) return false;
        // AsValueString respects unit settings; AsString is the raw form. Try value first.
        var value = p.AsValueString();
        if (string.IsNullOrEmpty(value)) value = p.AsString();
        if (string.IsNullOrEmpty(value)) return false;
        props[key] = value!;
        return true;
    }
}
