using System.Reflection;

namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Outcome of compiling an AI-generated C# snippet via <see cref="RoslynCompiler"/>.
/// Either the compilation produced a loadable assembly with a callable entry point
/// (<see cref="IsSuccess"/> = true), or it failed with one or more <see cref="CompilerError"/>s.
/// </summary>
public sealed class CompilationResult
{
    /// <summary>True when the snippet compiled cleanly and an assembly is loaded in memory.</summary>
    public bool IsSuccess => Errors.Count == 0 && CompiledAssembly != null;

    /// <summary>Compiled assembly when <see cref="IsSuccess"/> is true. Null otherwise.</summary>
    public Assembly? CompiledAssembly { get; set; }

    /// <summary>
    /// Fully-qualified name of the type containing the entry point. Always
    /// <c>BIManage.AI.Terminal.Generated.Script</c> for our compiler — exposed as a property
    /// rather than a constant so callers don't hard-code it and so a future test harness
    /// could point at a different generated type if needed.
    /// </summary>
    public string EntryPointTypeName { get; set; } = "BIManage.AI.Terminal.Generated.Script";

    /// <summary>Method name to invoke on <see cref="EntryPointTypeName"/>. Always <c>Run</c>.</summary>
    public string EntryPointMethodName { get; set; } = "Run";

    /// <summary>Compilation errors when <see cref="IsSuccess"/> is false. Empty otherwise.</summary>
    public List<CompilerError> Errors { get; } = new();

    /// <summary>Multi-line summary suitable for embedding in the tool result the LLM sees.</summary>
    public string DetailedReport()
    {
        if (IsSuccess) return "Compilation succeeded.";
        var lines = new List<string>(Errors.Count + 1)
        {
            $"Compilation failed with {Errors.Count} error(s):"
        };
        for (int i = 0; i < Errors.Count; i++)
        {
            var e = Errors[i];
            lines.Add($"  {i + 1}. line {e.Line}, col {e.Column}: {e.Id} {e.Message}");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// A single compiler diagnostic projected into a wire-friendly shape. The LLM reads these
/// from the tool result and corrects its code.
/// </summary>
public sealed class CompilerError
{
    /// <summary>Roslyn diagnostic id, e.g. <c>CS0103</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>1-based user-line where the error appears (already translated out of the wrapper).</summary>
    public int Line { get; init; }

    /// <summary>1-based column.</summary>
    public int Column { get; init; }

    /// <summary>Human-readable description copied from Roslyn.</summary>
    public string Message { get; init; } = string.Empty;
}
