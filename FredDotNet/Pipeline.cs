// FredPipeline - Composable streaming pipeline for sed, grep, awk, and find operations.

namespace FredDotNet;

/// <summary>
/// Stage in a Fred processing pipeline.
/// </summary>
public interface IPipelineStage
{
    /// <summary>
    /// Process input from reader and write output to writer.
    /// Returns an exit code (0 = success/match, 1 = no match, 2 = error).
    /// </summary>
    int Execute(TextReader input, TextWriter output);
}

/// <summary>
/// Builder for composing sed, grep, awk, and find operations into a streaming pipeline.
/// Stages are executed in order; each stage's output feeds the next stage's input.
/// </summary>
public sealed class FredPipeline
{
    private readonly List<IPipelineStage> _stages = new();

    private FredPipeline() { }

    /// <summary>
    /// Create a new empty pipeline.
    /// </summary>
    public static FredPipeline Create() => new();

    /// <summary>
    /// Add a sed stage by compiling a script string.
    /// </summary>
    public FredPipeline Sed(string script, bool suppressDefault = false, bool useEre = false)
    {
        var compiled = SedParser.Parse(script, suppressDefault, useEre);
        _stages.Add(new SedPipelineStage(compiled));
        return this;
    }

    /// <summary>
    /// Add a sed stage using a pre-compiled SedScript.
    /// </summary>
    public FredPipeline Sed(SedScript script)
    {
        _stages.Add(new SedPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add an awk stage by compiling a program string.
    /// </summary>
    public FredPipeline Awk(string program, string? fieldSeparator = null)
    {
        var compiled = AwkEngine.Compile(program);
        _stages.Add(new AwkPipelineStage(compiled, fieldSeparator));
        return this;
    }

    /// <summary>
    /// Add an awk stage using a pre-compiled AwkScript.
    /// </summary>
    public FredPipeline Awk(AwkScript script)
    {
        _stages.Add(new AwkPipelineStage(script, fieldSeparator: null));
        return this;
    }

    /// <summary>
    /// Add a grep stage by compiling a pattern string with default BRE mode.
    /// </summary>
    public FredPipeline Grep(string pattern, bool ignoreCase = false, bool invertMatch = false)
    {
        var script = GrepEngine.Compile(pattern, ignoreCase: ignoreCase, invertMatch: invertMatch);
        _stages.Add(new GrepPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a grep stage using GrepOptions for full control.
    /// </summary>
    public FredPipeline Grep(GrepOptions options)
    {
        var script = GrepEngine.Compile(options);
        _stages.Add(new GrepPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a grep stage using a pre-compiled GrepScript.
    /// </summary>
    public FredPipeline Grep(GrepScript script)
    {
        _stages.Add(new GrepPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a find stage that outputs matching file paths (one per line).
    /// Parses find-style CLI arguments.
    /// </summary>
    public FredPipeline Find(params string[] args)
    {
        var script = FindEngine.Compile(args);
        _stages.Add(new FindPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a find stage using FindOptions.
    /// </summary>
    public FredPipeline Find(FindOptions options)
    {
        var script = FindEngine.Compile(options);
        _stages.Add(new FindPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a find stage using a pre-compiled FindScript.
    /// </summary>
    public FredPipeline Find(FindScript script)
    {
        _stages.Add(new FindPipelineStage(script));
        return this;
    }

    /// <summary>
    /// Add a custom pipeline stage.
    /// </summary>
    public FredPipeline Stage(IPipelineStage stage)
    {
        _stages.Add(stage);
        return this;
    }

    /// <summary>
    /// Execute the pipeline on a string input and return the result.
    /// </summary>
    public string Execute(string input)
    {
        var sw = new StringWriter();
        Execute(new StringReader(input), sw);
        return sw.ToString();
    }

    /// <summary>
    /// Execute the pipeline, reading from input and writing to output.
    /// </summary>
    public void Execute(TextReader input, TextWriter output)
    {
        if (_stages.Count == 0)
        {
            // Empty pipeline: pass through
            output.Write(input.ReadToEnd());
            return;
        }

        TextReader current = input;
        for (int i = 0; i < _stages.Count; i++)
        {
            if (i == _stages.Count - 1)
            {
                // Last stage writes directly to final output
                _stages[i].Execute(current, output);
            }
            else
            {
                // Intermediate stage: capture output, feed to next stage
                var sw = new StringWriter();
                _stages[i].Execute(current, sw);
                current = new StringReader(sw.ToString());
            }
        }
    }
}

/// <summary>
/// Pipeline stage adapter for SedScript.
/// </summary>
internal sealed class SedPipelineStage : IPipelineStage
{
    private readonly SedScript _script;

    public SedPipelineStage(SedScript script) => _script = script;

    public int Execute(TextReader input, TextWriter output)
    {
        string result = _script.Transform(input.ReadToEnd());
        output.Write(result);
        return 0;
    }
}

/// <summary>
/// Pipeline stage adapter for AwkScript.
/// </summary>
internal sealed class AwkPipelineStage : IPipelineStage
{
    private readonly AwkScript _script;
    private readonly string? _fieldSeparator;

    public AwkPipelineStage(AwkScript script, string? fieldSeparator)
    {
        _script = script;
        _fieldSeparator = fieldSeparator;
    }

    public int Execute(TextReader input, TextWriter output)
    {
        var (result, exitCode) = _script.Execute(input.ReadToEnd(), _fieldSeparator);
        output.Write(result);
        return exitCode;
    }
}

/// <summary>
/// Pipeline stage adapter for GrepScript.
/// Grep is the only engine that truly streams line-at-a-time through the pipeline.
/// </summary>
internal sealed class GrepPipelineStage : IPipelineStage
{
    private readonly GrepScript _script;

    public GrepPipelineStage(GrepScript script) => _script = script;

    public int Execute(TextReader input, TextWriter output)
    {
        return _script.Execute(input, output);
    }
}

/// <summary>
/// Pipeline stage adapter for FindScript.
/// Find is a source stage — it ignores input and produces file paths as output.
/// </summary>
internal sealed class FindPipelineStage : IPipelineStage
{
    private readonly FindScript _script;

    public FindPipelineStage(FindScript script) => _script = script;

    public int Execute(TextReader input, TextWriter output)
    {
        // Find ignores the input reader — it generates paths from the filesystem
        int count = _script.Execute(output);
        return count > 0 ? 0 : 1;
    }
}
