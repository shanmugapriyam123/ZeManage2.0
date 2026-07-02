using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Static analyzer for AI-generated C# snippets. Parses the supplied code into a syntax
/// tree, walks every node, and returns a <see cref="SafetyVerdict"/> listing every
/// unsafe construct found. The compiler is invoked only after this analyzer returns
/// <see cref="SafetyVerdict.IsAllowed"/> == true.
/// </summary>
/// <remarks>
/// <para>
/// Design philosophy: deny-by-default for namespaces, deny-by-pattern for specific risky
/// calls (Transaction, Delete, Parameter.Set, etc.). The combination protects against
/// both well-known mutation paths AND sneaky bypasses via reflection or interop.
/// </para>
/// <para>
/// All rule ids follow the pattern <c>SAFE###_ShortName</c>. Stable across versions —
/// downstream telemetry, audit logs, and LLM prompts key on these. Don't rename existing ones.
/// </para>
/// </remarks>
public static class RoslynStaticAnalyzer
{
    /// <summary>
    /// Analyzes the supplied C# snippet and returns a verdict. The code is expected to be
    /// the body of <see cref="ScriptExecutionContext"/>'s entry point — the analyzer wraps
    /// it in a parseable form internally before walking the tree.
    /// </summary>
    /// <param name="userCode">
    /// Raw C# snippet from the LLM. Typically a few lines that build a FilteredElementCollector
    /// query, project the results, and return them.
    /// </param>
    public static SafetyVerdict Analyze(string userCode)
    {
        var verdict = new SafetyVerdict();

        if (string.IsNullOrWhiteSpace(userCode))
        {
            verdict.Rejections.Add(new SafetyRejection
            {
                RuleId = "SAFE000_EmptyCode",
                Line = 0,
                Message = "Code is empty. Provide a non-empty C# snippet that returns a value."
            });
            return verdict;
        }

        // Wrap the snippet in a parseable shell — analyzer doesn't care about semantics yet,
        // it just needs the parser to give us a valid syntax tree. Wrapping shape mirrors
        // what RoslynCompiler will eventually emit, so line numbers stay consistent.
        var wrapped = WrapForParsing(userCode);
        var tree = CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions(LanguageVersion.Latest));
        var root = tree.GetRoot();

        // Parse errors are NOT analyzer rejections — the compiler will report them with
        // proper line numbers. We only short-circuit on egregious parse failures so the
        // walker doesn't crash on null nodes.
        if (root == null)
        {
            verdict.Rejections.Add(new SafetyRejection
            {
                RuleId = "SAFE001_UnparseableCode",
                Line = 0,
                Message = "Code could not be parsed as C#. Check syntax (matched braces, semicolons, etc.)."
            });
            return verdict;
        }

        // Run the walker. Each Visit* method may append to verdict.Rejections.
        var walker = new SafetyWalker(verdict);
        walker.Visit(root);
        return walker.Verdict;
    }

    /// <summary>
    /// Wraps the user's snippet inside a minimal compilation unit so Roslyn can parse it.
    /// The wrapper deliberately mirrors what <see cref="RoslynCompiler"/> will generate, so
    /// line numbers in rejection messages line up with what the LLM submitted.
    /// </summary>
    private static string WrapForParsing(string userCode)
        => @"using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace BIManage.AI.Terminal.Generated {
  public static class Script {
    public static object Run(Document doc, UIApplication uiApp) {
" + userCode + @"
    }
  }
}";
}

/// <summary>
/// The CSharpSyntaxWalker that does the actual rejection work. One class instance per
/// analysis run — accumulates rejections into the supplied <see cref="SafetyVerdict"/>.
/// </summary>
internal sealed class SafetyWalker : CSharpSyntaxWalker
{
    public SafetyVerdict Verdict { get; }

    public SafetyWalker(SafetyVerdict verdict) : base(SyntaxWalkerDepth.Node)
    {
        Verdict = verdict;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Namespace allowlist enforcement
    // ─────────────────────────────────────────────────────────────────────────────

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        // The wrapper adds its own using directives — they appear at line 0 in the
        // wrapper, well before the user's code starts. Skip them.
        if (node.GetLocation().GetLineSpan().StartLinePosition.Line < UserCodeStartLine)
        {
            base.VisitUsingDirective(node);
            return;
        }

        var name = node.Name?.ToString();
        if (!NamespaceAllowlist.IsAllowed(name))
        {
            Reject("SAFE002_DisallowedNamespace", node,
                $"using '{name}' is not on the allowlist. Allowed: {NamespaceAllowlist.AllowedAsCommaList()}.");
        }
        base.VisitUsingDirective(node);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mutation rejection — Transaction, Delete, Parameter.Set, etc.
    // ─────────────────────────────────────────────────────────────────────────────

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var typeName = node.Type.ToString();

        // `new Transaction(...)` and friends — the canonical mutation entry point.
        if (typeName == "Transaction" || typeName.EndsWith(".Transaction", StringComparison.Ordinal) ||
            typeName == "SubTransaction" || typeName.EndsWith(".SubTransaction", StringComparison.Ordinal) ||
            typeName == "TransactionGroup" || typeName.EndsWith(".TransactionGroup", StringComparison.Ordinal))
        {
            Reject("SAFE020_NoTransaction", node,
                $"Creating a {typeName} is not allowed. AI-generated code is read-only by design — " +
                "do not start, commit, or roll back transactions. If you need to modify the model, " +
                "ask the user to use a regular Revit command instead.");
        }

        // `new HttpClient()` etc — even though System.Net isn't on the allowlist, this catches
        // type names that might be reachable via fully qualified names without an explicit using.
        if (typeName.EndsWith("HttpClient", StringComparison.Ordinal) ||
            typeName.EndsWith("WebClient", StringComparison.Ordinal) ||
            typeName.EndsWith("WebRequest", StringComparison.Ordinal) ||
            typeName.EndsWith("HttpRequestMessage", StringComparison.Ordinal))
        {
            Reject("SAFE011_NoNetwork", node,
                $"Creating a {typeName} is not allowed. AI-generated code may not make network calls.");
        }

        // `new Process()` — process launching.
        if (typeName == "Process" || typeName.EndsWith(".Process", StringComparison.Ordinal))
        {
            Reject("SAFE012_NoProcess", node,
                "Creating a Process is not allowed. AI-generated code may not launch subprocesses.");
        }

        base.VisitObjectCreationExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var expressionText = node.Expression.ToString();

        // Document.Delete (the irreversible one)
        if (EndsWithMember(expressionText, "Delete"))
        {
            // Catches doc.Delete, document.Delete, Document.Delete, and chained variants.
            // false positives possible (e.g. a Delete method on user code) but we err on the
            // side of caution — the LLM gets a clear rejection and can rename if needed.
            Reject("SAFE021_NoDelete", node,
                "Calling Delete on a Document is not allowed. AI-generated code is read-only.");
        }

        // Parameter.Set / SetValueString — element mutation
        if (EndsWithMember(expressionText, "Set") ||
            EndsWithMember(expressionText, "SetValueString") ||
            EndsWithMember(expressionText, "SetParameterByName"))
        {
            // Set is broad — could be HashSet.Set etc. We accept some false positives because
            // the alternatives (proper semantic analysis with a Compilation) cost compile time
            // and the LLM can easily rename to avoid the trigger.
            Reject("SAFE022_NoParameterMutation", node,
                $"Method call '{expressionText}' looks like a parameter or element mutation. " +
                "AI-generated code may not modify element parameters or properties. " +
                "If the method name is innocent (e.g. on a HashSet), rename or use a different call to bypass this check.");
        }

        // Process.Start (static method)
        if (expressionText.EndsWith("Process.Start", StringComparison.Ordinal) ||
            expressionText.EndsWith("Process.GetProcesses", StringComparison.Ordinal) ||
            expressionText.EndsWith("Process.GetCurrentProcess", StringComparison.Ordinal))
        {
            Reject("SAFE012_NoProcess", node,
                $"Calling {expressionText} is not allowed. AI-generated code may not interact with subprocesses.");
        }

        // Assembly.Load / Activator.CreateInstance / Type.GetType — reflection bypass
        if (expressionText.EndsWith("Assembly.Load", StringComparison.Ordinal) ||
            expressionText.EndsWith("Assembly.LoadFile", StringComparison.Ordinal) ||
            expressionText.EndsWith("Assembly.LoadFrom", StringComparison.Ordinal) ||
            expressionText.EndsWith("Activator.CreateInstance", StringComparison.Ordinal) ||
            expressionText.EndsWith("Type.GetType", StringComparison.Ordinal))
        {
            Reject("SAFE013_NoReflection", node,
                $"Calling {expressionText} is not allowed. AI-generated code may not use reflection " +
                "or dynamic assembly loading — these can bypass other safety checks.");
        }

        // File / Directory / Path I/O
        if (StartsWithType(expressionText, "File.") ||
            StartsWithType(expressionText, "Directory.") ||
            StartsWithType(expressionText, "Path.") ||
            StartsWithType(expressionText, "FileInfo.") ||
            StartsWithType(expressionText, "DirectoryInfo."))
        {
            Reject("SAFE010_NoFileIO", node,
                $"Calling {expressionText} is not allowed. AI-generated code may not perform file I/O.");
        }

        // Environment.Exit / Environment.FailFast etc
        if (expressionText.EndsWith("Environment.Exit", StringComparison.Ordinal) ||
            expressionText.EndsWith("Environment.FailFast", StringComparison.Ordinal))
        {
            Reject("SAFE016_NoEnvironmentExit", node,
                $"Calling {expressionText} is not allowed.");
        }

        base.VisitInvocationExpression(node);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Language-feature rejections
    // ─────────────────────────────────────────────────────────────────────────────

    public override void VisitUnsafeStatement(UnsafeStatementSyntax node)
    {
        Reject("SAFE030_NoUnsafe", node,
            "The 'unsafe' keyword is not allowed in AI-generated code.");
        base.VisitUnsafeStatement(node);
    }

    public override void VisitFixedStatement(FixedStatementSyntax node)
    {
        Reject("SAFE031_NoFixed", node,
            "The 'fixed' statement is not allowed in AI-generated code.");
        base.VisitFixedStatement(node);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Line number where the user's snippet begins in the wrapped parse buffer.
    /// Counted by the lines in <see cref="RoslynStaticAnalyzer.WrapForParsing"/>'s prologue
    /// — kept as a constant so we don't drift if that prologue grows.
    /// </summary>
    /// <remarks>
    /// Current prologue is 8 lines:
    ///   1: using System;
    ///   2: using System.Collections.Generic;
    ///   3: using System.Linq;
    ///   4: using Autodesk.Revit.DB;
    ///   5: using Autodesk.Revit.UI;
    ///   6: namespace BIManage.AI.Terminal.Generated {
    ///   7:   public static class Script {
    ///   8:     public static object Run(Document doc, UIApplication uiApp) {
    /// User code starts at line 9 (index 8 zero-based).
    /// </remarks>
    private const int UserCodeStartLine = 8;

    /// <summary>
    /// Returns true when the expression text ends with <c>.MemberName</c> at the very end
    /// — i.e. the final dotted segment matches. Avoids false positives on substrings like
    /// "SetupSomething" matching "Set".
    /// </summary>
    private static bool EndsWithMember(string expression, string memberName)
    {
        if (string.IsNullOrEmpty(expression) || string.IsNullOrEmpty(memberName)) return false;
        var dotMember = "." + memberName;
        return expression.EndsWith(dotMember, StringComparison.Ordinal);
    }

    /// <summary>True when the expression's leading segment matches a known dangerous static type.</summary>
    private static bool StartsWithType(string expression, string typePrefix)
        => !string.IsNullOrEmpty(expression) && expression.StartsWith(typePrefix, StringComparison.Ordinal);

    /// <summary>Appends a rejection with the node's line number translated back to the user's coordinate space.</summary>
    private void Reject(string ruleId, SyntaxNode node, string message)
    {
        var rawLine = node.GetLocation().GetLineSpan().StartLinePosition.Line;
        // Translate wrapper-line back to user-line. User line 1 lives at wrapper line UserCodeStartLine.
        var userLine = Math.Max(1, rawLine - UserCodeStartLine + 1);
        Verdict.Rejections.Add(new SafetyRejection
        {
            RuleId = ruleId,
            Line = userLine,
            Message = message
        });
    }
}
