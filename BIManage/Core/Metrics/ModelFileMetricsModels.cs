using System;
using System.Collections.Generic;

namespace BIManage.Core.Metrics
{
    /// <summary>
    /// Fast metrics captured before sync/save operations (20 metrics)
    /// Performance: Negligible to Low-Medium impact
    /// </summary>
    public class FastMetrics
    {
        // File and model info
        public long? FileSizeBytes { get; set; }
        public int? LevelsCount { get; set; }
        public int? GridsCount { get; set; }
        public int? DesignOptionsCount { get; set; }

        // Links and imports
        public int? LinkedDwgCount { get; set; }
        public int? ImportedDwgCount { get; set; }
        public int? LinkedRevitCount { get; set; }
        public int? RasterImagesCount { get; set; }

        // Quality metrics
        public int? WarningsCount { get; set; }
        public int? DuplicateElementsCount { get; set; }

        // Groups and organization
        public int? ModelGroupsCount { get; set; }
        public int? DetailGroupsCount { get; set; }
        public int? TotalViewsCount { get; set; }
        public int? SheetsCount { get; set; }
        public int? TotalFamiliesCount { get; set; }
        public int? TotalWorksetsCount { get; set; }

        // Object styles and templates
        public int? NonNativeObjectStylesCount { get; set; }
        public int? ViewTemplatesCount { get; set; }

        // Shared coordinates (values stored in project display units; unit label in SharedCoordUnit)
        public double? SharedCoordNs { get; set; }
        public double? SharedCoordEw { get; set; }
        public double? SharedCoordElevation { get; set; }
        public string? SharedCoordUnit { get; set; }
    }

    /// <summary>
    /// Medium-cost metrics captured periodically (10 metrics)
    /// Performance: Medium to Medium-High impact
    /// </summary>
    public class MediumMetrics
    {
        // Element counts
        public int? TotalElementsCount { get; set; }
        public int? ModelElementsCount { get; set; }
        public int? AnnotativeElementsCount { get; set; }
        public int? InplaceFamiliesCount { get; set; }

        // Room metrics
        public int? UnplacedRoomsCount { get; set; }
        public int? UnenclosedRoomsCount { get; set; }

        // View metrics
        public int? ViewsNotOnSheetsCount { get; set; }

        // MEP and structural (from warnings)
        public int? WallsNotConnectedCount { get; set; }
        public int? PipesNotConnectedCount { get; set; }
        public int? DuctsNotConnectedCount { get; set; }
    }

    /// <summary>
    /// Expensive metrics captured only on manual user request (2 metrics)
    /// Performance: High to Very-High impact
    /// </summary>
    public class ExpensiveMetrics
    {
        // Heavy computational metrics
        public int? FamiliesOver5MbCount { get; set; }
        public int? PurgeableElementsCount { get; set; }
    }

    /// <summary>
    /// Detailed list data for the detailed report. Only collected when "Generate Detailed Report" is checked.
    /// </summary>
    public class DetailedMetrics
    {
        // General Statistics
        public List<string> Levels { get; set; } = new List<string>();
        public List<string> Grids { get; set; } = new List<string>();
        public List<string> LinkedRevitModels { get; set; } = new List<string>();
        public List<string> LinkedDwgFiles { get; set; } = new List<string>();
        public List<string> ImportedDwgFiles { get; set; } = new List<string>();
        public List<string> RasterImages { get; set; } = new List<string>();

        // Element Breakdown
        public List<string> InPlaceFamilyNames { get; set; } = new List<string>();

        // Views & Sheets
        public List<(string Name, string Type)> Views { get; set; } = new List<(string, string)>();
        public List<string> Sheets { get; set; } = new List<string>();
        public List<string> ViewsNotOnSheets { get; set; } = new List<string>();
        public List<string> ViewTemplates { get; set; } = new List<string>();

        // Performance
        public List<(string Message, int Count)> WarningsByType { get; set; } = new List<(string, int)>();
        public List<string> NonNativeObjectStyles { get; set; } = new List<string>();

        // Worksets & Families
        public List<string> Worksets { get; set; } = new List<string>();
        public List<(string Name, long SizeKB)> FamilySizes { get; set; } = new List<(string, long)>();
        public List<string> FamilySizeErrors { get; set; } = new List<string>();
        public long TotalFamilySizeKB { get; set; }

        // Best Practice
        public List<string> ModelGroups { get; set; } = new List<string>();
        public List<string> DetailGroups { get; set; } = new List<string>();
        public List<string> DesignOptions { get; set; } = new List<string>();

        // Quality
        public List<string> UnplacedRooms { get; set; } = new List<string>();
        public List<string> UnenclosedRooms { get; set; } = new List<string>();
    }
}
