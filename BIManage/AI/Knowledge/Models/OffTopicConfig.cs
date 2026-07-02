using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class OffTopicConfig
    {
        public List<string> RevitKeywords { get; set; } = new();
        public List<string> OffTopicKeywords { get; set; } = new();
        public int MinimumQuestionLength { get; set; } = 15;
        public int OffTopicMinimumLength { get; set; } = 30;
        public List<string> OffTopicReplies { get; set; } = new();
        public List<string> RedirectKeywords { get; set; } = new();
        public List<string> RedirectReplies { get; set; } = new();
    }
}
