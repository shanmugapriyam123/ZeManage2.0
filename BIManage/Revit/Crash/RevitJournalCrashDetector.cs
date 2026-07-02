using System;
using System.IO;
using System.Text;

namespace BIManage.Revit.Crash
{
    /// <summary>Result of analyzing a Revit journal file for crash evidence.</summary>
    public sealed class CrashEvidence
    {
        /// <summary>True when definitive crash keywords were found in the journal.</summary>
        public bool IsDefinitiveCrash { get; set; }

        /// <summary>True when ID_APP_EXIT was found — indicates a clean Revit exit.</summary>
        public bool HasCleanExit { get; set; }

        /// <summary>The keyword that triggered IsDefinitiveCrash, or null if none.</summary>
        public string? KeywordFound { get; set; }

        /// <summary>Journal file could not be located or read.</summary>
        public bool JournalNotFound { get; set; }

        /// <summary>
        /// True when the session should be considered crashed — either definitive crash
        /// keywords were found, or the journal has no clean exit (process died without
        /// ID_APP_EXIT, indicating force-kill, power loss, or unrecognized crash).
        /// </summary>
        public bool IsCrashed => JournalNotFound ? false : (IsDefinitiveCrash || !HasCleanExit);
    }

    /// <summary>
    /// Analyzes a Revit journal file to determine whether a session ended in a definitive
    /// crash (fatal error dialog) or an ambiguous/unknown termination (force-kill, power loss).
    /// </summary>
    public static class RevitJournalCrashDetector
    {
        // Definitive crash keywords — any match → Crashed (2)
        private static readonly string[] CrashKeywords =
        {
            "ExceptionCode=",           // .NET / Win32 exception code recorded
            "DBG_WARN: Missing ESSchema", // Schema corruption crash (common in 2024.3+)
            "DBG_ERROR",                // Serious error logged by Revit kernel
            "captureTryCatch",          // Exception handler fired without recovery
        };

        // Presence of this string → clean exit (not a crash)
        private const string CleanExitMarker = "ID_APP_EXIT";

        // Number of lines to read from the tail of the journal
        private const int TailLineCount = 200;

        /// <summary>
        /// Returns the expected journal directory for a given Revit version.
        /// </summary>
        public static string GetJournalDirectory(string revitVersion)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Autodesk", "Revit",
                $"Autodesk Revit {revitVersion}", "Journals");
        }

        /// <summary>
        /// Analyzes the journal file for the given session.
        /// </summary>
        /// <param name="journalFileName">Filename only (stored in sessions.journal_file_name).</param>
        /// <param name="revitVersion">Revit version string, e.g. "2025" (stored in sessions.revit_version).</param>
        public static CrashEvidence Analyze(string? journalFileName, string? revitVersion)
        {
            if (string.IsNullOrEmpty(journalFileName) || string.IsNullOrEmpty(revitVersion))
                return new CrashEvidence { JournalNotFound = true };

            var journalDir  = GetJournalDirectory(revitVersion);
            var journalPath = Path.Combine(journalDir, journalFileName);

            return AnalyzeFile(journalPath);
        }

        /// <summary>Analyzes a journal file at the given full path.</summary>
        public static CrashEvidence AnalyzeFile(string journalPath)
        {
            if (!File.Exists(journalPath))
                return new CrashEvidence { JournalNotFound = true };

            try
            {
                var tailLines = ReadTailLines(journalPath, TailLineCount);

                var hasCleanExit       = false;
                var isDefinitiveCrash  = false;
                string? keywordFound   = null;

                foreach (var line in tailLines)
                {
                    if (!hasCleanExit && line.Contains(CleanExitMarker, StringComparison.OrdinalIgnoreCase))
                        hasCleanExit = true;

                    if (!isDefinitiveCrash)
                    {
                        foreach (var kw in CrashKeywords)
                        {
                            if (line.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                isDefinitiveCrash = true;
                                keywordFound      = kw;
                                break;
                            }
                        }
                    }

                    if (hasCleanExit && isDefinitiveCrash) break;
                }

                return new CrashEvidence
                {
                    IsDefinitiveCrash = isDefinitiveCrash,
                    HasCleanExit      = hasCleanExit,
                    KeywordFound      = keywordFound
                };
            }
            catch
            {
                // Unreadable — treat as inconclusive
                return new CrashEvidence { JournalNotFound = true };
            }
        }

        /// <summary>
        /// Efficiently reads the last <paramref name="lineCount"/> lines of a file
        /// using a backward byte scan. Works on files held open by Revit (FileShare.ReadWrite).
        /// </summary>
        private static string[] ReadTailLines(string path, int lineCount)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length == 0) return Array.Empty<string>();

            var lines        = new System.Collections.Generic.List<string>(lineCount + 1);
            var currentLine  = new StringBuilder();
            var buffer       = new byte[4096];
            var position     = fs.Length;

            while (position > 0 && lines.Count < lineCount)
            {
                var readSize = (int)Math.Min(buffer.Length, position);
                position -= readSize;
                fs.Seek(position, SeekOrigin.Begin);
                fs.Read(buffer, 0, readSize);

                for (var i = readSize - 1; i >= 0; i--)
                {
                    var ch = (char)buffer[i];
                    if (ch == '\n')
                    {
                        if (currentLine.Length > 0)
                        {
                            var lineStr = ReverseString(currentLine.ToString()).TrimEnd('\r');
                            lines.Add(lineStr);
                            currentLine.Clear();
                            if (lines.Count >= lineCount) break;
                        }
                    }
                    else
                    {
                        currentLine.Append(ch);
                    }
                }
            }

            if (currentLine.Length > 0)
                lines.Add(ReverseString(currentLine.ToString()).TrimEnd('\r'));

            lines.Reverse(); // chronological order (oldest first within the tail)
            return lines.ToArray();
        }

        private static string ReverseString(string s)
        {
            var arr = s.ToCharArray();
            Array.Reverse(arr);
            return new string(arr);
        }
    }
}
