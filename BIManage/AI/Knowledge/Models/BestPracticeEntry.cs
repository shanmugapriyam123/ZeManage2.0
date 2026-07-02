using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class BestPracticeEntry
    {
        public List<string> Keywords { get; set; } = new();
        public string Title { get; set; } = string.Empty;
        public List<string> Practices { get; set; } = new();
    }

    internal class BestPracticesFile
    {
        public List<BestPracticeEntry> Entries { get; set; } = new();
    }
}
