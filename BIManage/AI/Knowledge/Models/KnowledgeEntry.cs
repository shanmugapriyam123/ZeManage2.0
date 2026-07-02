using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class KnowledgeEntry
    {
        public List<string> Keywords { get; set; } = new();
        public string Title { get; set; } = string.Empty;
        public string Solution { get; set; } = string.Empty;
    }

    internal class ErrorsFile
    {
        public List<KnowledgeEntry> Entries { get; set; } = new();
    }
}
