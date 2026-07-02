using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class WorkflowEntry
    {
        public List<string> Keywords { get; set; } = new();
        public string Title { get; set; } = string.Empty;
        public List<string> Steps { get; set; } = new();
    }

    internal class WorkflowsFile
    {
        public List<WorkflowEntry> Entries { get; set; } = new();
    }
}
