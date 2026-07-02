using System.Collections.Generic;

namespace BIManage.AI.Knowledge.Models
{
    public class ProductEntry
    {
        public List<string> Keywords { get; set; } = new();
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Minimum access level: "user" (all roles) or "admin" (Company/Project Admin only).
        /// Defaults to "user" if not specified.
        /// </summary>
        public string Access { get; set; } = "user";
    }

    internal class ProductFile
    {
        public List<ProductEntry> Entries { get; set; } = new();
    }
}
