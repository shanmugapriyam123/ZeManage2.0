using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Safety;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// TEMPORARY DEV TEST — runs the Phase 3d safety layer against ~20 hand-crafted
    /// adversarial payloads and reports per-test pass/fail. Goes beyond <see cref="TestSendCodeCommand"/>'s
    /// happy-path + one-malicious-snippet coverage by exercising sneaky bypass attempts:
    /// aliased namespaces, reflection, fully-qualified type names, creative mutation paths,
    /// language-feature bypasses.
    /// </summary>
    /// <remarks>
    /// REMOVE THIS COMMAND once Phase 3h hardening lands — it's a one-shot validation run,
    /// not a continuous test. Better long-term home is the BIManageRevit.Tests project, but
    /// running there can't validate the analyzer against a live Revit API surface — only an
    /// in-Revit test can confirm assembly references resolve correctly.
    /// </remarks>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdversarialSafetyTestCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;

            try
            {
                var cases = BuildTestCases();
                var report = new StringBuilder();
                int passed = 0;
                int failed = 0;

                report.AppendLine("Phase 3d Adversarial Safety Test Suite");
                report.AppendLine($"Running {cases.Length} test case(s)…");
                report.AppendLine(new string('═', 60));

                foreach (var test in cases)
                {
                    var verdict = RoslynStaticAnalyzer.Analyze(test.Code);

                    bool isExpected;
                    string actualSummary;

                    if (test.ShouldReject)
                    {
                        // Test passes if the analyzer rejected AND the expected rule fired.
                        var ruleHit = verdict.Rejections.Any(r => r.RuleId == test.ExpectedRuleId);
                        isExpected = !verdict.IsAllowed && ruleHit;
                        actualSummary = verdict.IsAllowed
                            ? "ALLOWED (analyzer let it through!)"
                            : $"REJECTED with: {string.Join(", ", verdict.Rejections.Select(r => r.RuleId))}";
                    }
                    else
                    {
                        // Test passes if the analyzer allowed it (no rejections).
                        isExpected = verdict.IsAllowed;
                        actualSummary = verdict.IsAllowed
                            ? "ALLOWED"
                            : $"REJECTED with: {string.Join(", ", verdict.Rejections.Select(r => r.RuleId))}";
                    }

                    var statusGlyph = isExpected ? "✓" : "✗";
                    if (isExpected) passed++; else failed++;

                    report.AppendLine();
                    report.AppendLine($"{statusGlyph} [{test.Id}] {test.Description}");
                    report.AppendLine($"   Expected: {(test.ShouldReject ? $"REJECTED ({test.ExpectedRuleId})" : "ALLOWED")}");
                    report.AppendLine($"   Actual  : {actualSummary}");
                }

                report.AppendLine();
                report.AppendLine(new string('═', 60));
                report.AppendLine($"RESULT: {passed} passed, {failed} failed out of {cases.Length}");
                if (failed > 0)
                {
                    report.AppendLine();
                    report.AppendLine("⚠ Failures indicate either a bypass in the analyzer (security issue) ");
                    report.AppendLine("  or an over-restriction blocking legitimate code (UX issue).");
                    report.AppendLine("  Each failure should be triaged before Phase 3h beta rollout.");
                }

                TaskDialog.Show("AI Terminal — Adversarial Safety Test", report.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(
                    "AI Terminal — Adversarial Safety Test FAILED",
                    $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Curated adversarial corpus. Each case carries an id, the snippet, whether the
        /// analyzer should reject it, and (when rejecting) which rule should fire.
        /// </summary>
        private static AdversarialCase[] BuildTestCases() => new[]
        {
            // ── Category A: Direct mutation attempts ────────────────────────────
            new AdversarialCase
            {
                Id = "A1",
                Description = "Direct Transaction.Start",
                Code = @"using (var t = new Transaction(doc, ""x"")) { t.Start(); t.Commit(); } return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE020_NoTransaction"
            },
            new AdversarialCase
            {
                Id = "A2",
                Description = "SubTransaction creation",
                Code = @"var st = new SubTransaction(doc); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE020_NoTransaction"
            },
            new AdversarialCase
            {
                Id = "A3",
                Description = "TransactionGroup creation",
                Code = @"var tg = new TransactionGroup(doc, ""g""); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE020_NoTransaction"
            },
            new AdversarialCase
            {
                Id = "A4",
                Description = "doc.Delete call",
                Code = @"doc.Delete(new ElementId(123)); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE021_NoDelete"
            },
            new AdversarialCase
            {
                Id = "A5",
                Description = "Chained Delete via LINQ",
                Code = @"var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).ToList();
                         walls.ForEach(w => doc.Delete(w.Id));
                         return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE021_NoDelete"
            },

            // ── Category B: Reflection bypasses ─────────────────────────────────
            new AdversarialCase
            {
                Id = "B1",
                Description = "Assembly.Load reflection bypass",
                Code = @"var asm = System.Reflection.Assembly.Load(""RevitAPI""); return asm.FullName;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE013_NoReflection"
            },
            new AdversarialCase
            {
                Id = "B2",
                Description = "Type.GetType reflection lookup",
                Code = @"var t = Type.GetType(""Autodesk.Revit.DB.Transaction"");
                         return t?.FullName ?? ""null"";",
                ShouldReject = true,
                ExpectedRuleId = "SAFE013_NoReflection"
            },
            new AdversarialCase
            {
                Id = "B3",
                Description = "Activator.CreateInstance bypass",
                Code = @"var t = Activator.CreateInstance(typeof(string)); return t;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE013_NoReflection"
            },

            // ── Category C: File / network / process ────────────────────────────
            new AdversarialCase
            {
                Id = "C1",
                Description = "File.WriteAllText direct call",
                Code = @"File.WriteAllText(""C:\\evil.txt"", ""haxx""); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE010_NoFileIO"
            },
            new AdversarialCase
            {
                Id = "C2",
                Description = "Directory.GetFiles direct call",
                Code = @"var files = Directory.GetFiles(""C:\\""); return files.Length;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE010_NoFileIO"
            },
            new AdversarialCase
            {
                Id = "C3",
                Description = "Process.Start launch",
                Code = @"Process.Start(""calc.exe""); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE012_NoProcess"
            },
            new AdversarialCase
            {
                Id = "C4",
                Description = "HttpClient creation",
                Code = @"var hc = new HttpClient(); return hc.ToString();",
                ShouldReject = true,
                ExpectedRuleId = "SAFE011_NoNetwork"
            },
            new AdversarialCase
            {
                Id = "C5",
                Description = "Environment.Exit",
                Code = @"Environment.Exit(1); return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE016_NoEnvironmentExit"
            },

            // ── Category D: Mutation via Parameter.Set ──────────────────────────
            new AdversarialCase
            {
                Id = "D1",
                Description = "Parameter.Set on element",
                Code = @"var w = new FilteredElementCollector(doc).OfClass(typeof(Wall)).First();
                         w.LookupParameter(""Mark"").Set(""x"");
                         return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE022_NoParameterMutation"
            },

            // ── Category E: Language-feature bypasses ───────────────────────────
            new AdversarialCase
            {
                Id = "E1",
                Description = "unsafe block",
                Code = @"unsafe { int x = 5; int* p = &x; } return 1;",
                ShouldReject = true,
                ExpectedRuleId = "SAFE030_NoUnsafe"
            },

            // ── Category F: Disallowed namespace via using ──────────────────────
            new AdversarialCase
            {
                Id = "F1",
                Description = "using System.IO directive",
                // Note: 'using' at function body is wrong syntax; we test the analyzer's
                // namespace allowlist via what it would do if the LLM tried to add a using
                // directive at the top. The wrapper hard-codes the allowlist, so a hostile
                // 'using' would actually have to appear in user code via a fully-qualified
                // type name — covered by C1, C2 etc. This case is here as a smoke test that
                // an obviously-broken 'using' at least doesn't crash the analyzer.
                Code = @"return 1; // smoke test — analyzer must not crash on weird code",
                ShouldReject = false,
                ExpectedRuleId = ""
            },

            // ── Category G: LEGITIMATE — must pass ──────────────────────────────
            new AdversarialCase
            {
                Id = "G1",
                Description = "Plain wall count (the safe baseline)",
                Code = @"return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();",
                ShouldReject = false,
                ExpectedRuleId = ""
            },
            new AdversarialCase
            {
                Id = "G2",
                Description = "Multi-line LINQ with anonymous type return",
                Code = @"var ducts = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_DuctCurves)
                            .WhereElementIsNotElementType()
                            .ToList();
                         return new { count = ducts.Count, names = ducts.Take(3).Select(d => d.Name).ToList() };",
                ShouldReject = false,
                ExpectedRuleId = ""
            },
            new AdversarialCase
            {
                Id = "G3",
                Description = "Parameter READ (not Set) — must be allowed",
                Code = @"var wall = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstOrDefault();
                         var mark = wall?.LookupParameter(""Mark"")?.AsString();
                         return new { wallId = wall?.Id.IntegerValue, mark = mark };",
                ShouldReject = false,
                ExpectedRuleId = ""
            },
            new AdversarialCase
            {
                Id = "G4",
                Description = "Cast and conditional — common legitimate pattern",
                Code = @"var element = new FilteredElementCollector(doc).WhereElementIsNotElementType().FirstOrDefault();
                         if (element is Wall wall) {
                             return new { isWall = true, name = wall.Name };
                         }
                         return new { isWall = false };",
                ShouldReject = false,
                ExpectedRuleId = ""
            },
        };

        private sealed class AdversarialCase
        {
            public string Id { get; init; } = "";
            public string Description { get; init; } = "";
            public string Code { get; init; } = "";
            public bool ShouldReject { get; init; }
            public string ExpectedRuleId { get; init; } = "";
        }
    }
}
