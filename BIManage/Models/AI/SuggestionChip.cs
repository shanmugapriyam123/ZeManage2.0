namespace BIManage.Models.AI
{
    /// <summary>
    /// Data model for a suggestion card shown on the AI welcome screen.
    /// </summary>
    public class SuggestionChip
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Segoe MDL2 Assets icon glyph (e.g. "\xE8D4").</summary>
        public string IconGlyph { get; set; } = "\xE946";

        /// <summary>Icon foreground color hex (e.g. "#E6EAF0").</summary>
        public string IconColor { get; set; } = "#000000";

        /// <summary>Icon background color hex (e.g. "#E8F0FA").</summary>
        public string IconBackground { get; set; } = "#E8F0FA";
    }
}
