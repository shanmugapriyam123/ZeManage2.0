using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class PerformanceEntry
    {
        public List<string> Keywords { get; set; } = new();
        public string Title { get; set; } = string.Empty;
        public List<string> Steps { get; set; } = new();
    }

    internal class PerformanceTipsFile
    {
        public List<PerformanceEntry> Entries { get; set; } = new();
    }
}
