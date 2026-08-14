using SpecRunner.Core;
using SpecRunner.Graph;
using SpecRunner.Records;
using SpecRunner.State;
using SpecRunner.Workflow;

namespace SpecRunner.Reconcile;

/// <summary>Resolves the frozen iteration order for a step that iterates, by artifact id.</summary>
public delegate IReadOnlyList<string> IterationOrderLookup(string frozenListArtifactId);

/// <summary>
/// Feature 1.4 - a completion record lists every input it consumed: file path plus hash, and
/// variable name plus value hash. This class computes that list from a step's *declarations*,
/// which is what makes the graph a constraint rather than a description.
///
/// The same function serves reconciliation and execution. The pre-flight compares what it
/// computes here against what a record claims; the runner records what it computes here. If the
/// two ever came from different code, skipping would stop meaning what it says.
/// </summary>
public static class StepInputs
{
    public const string TemplateProducer = "template";

    public const string OperatorProducer = "operator";

    /// <summary>Filename prefix of an answer record, recognised when classifying a difference.</summary>
    public const string AnswerFilePrefix = RecordStore.AnswerKind + ".v";

    public static IReadOnlyList<InputRef> Compute(
        Step step,
        string? target,
        DependencyGraph graph,
        ProjectState state,
        ArtifactIndex artifacts,
        ProjectPaths paths,
        string promptsDirectory,
        RecordStore records,
        IterationOrderLookup iterationOrder)
    {
        var inputs = new List<InputRef>();

        // Feature 6.3 - the answer in force is an input of the step that consumed it. Without
        // this the record would not name the answer at all, and an edited answer would be neither
        // acted on nor reported: the step would skip on a record that no longer describes it.
        if (step.RaisesDecision)
        {
            var version = records.ReadInvalidations(step.Id, target).Count + 1;
            var answerPath = Path.Combine(paths.RecordDirectory(step.Id, target), RecordStore.AnswerFileName(version));
            if (File.Exists(answerPath))
            {
                inputs.Add(AnswerInput(paths, answerPath));
            }
        }

        // The template is an input like any other, so editing a template invalidates the step
        // that uses it (feature 4.3).
        if (step.PromptTemplate is { } template)
        {
            var absolute = Path.Combine(promptsDirectory, template);
            if (!File.Exists(absolute))
            {
                throw new HaltException($"Step '{step.Id}' declares prompt template '{template}', which is not at {absolute}.");
            }

            // The recorded name is a *logical* one: "prompts/" is the template namespace, not a
            // path relative to anything. The physical directory is configurable, so a real path
            // here would differ between operators running the same workflow and would make two
            // otherwise identical records disagree. The hash identifies the content; the startup
            // config echo says where the namespace resolved.
            inputs.Add(new InputRef(InputRef.FileKind, $"prompts/{template}", Canonical.HashFile(absolute), TemplateProducer));
        }

        foreach (var artifactId in step.ReadsArtifacts)
        {
            var producerId = graph.ArtifactProducers[artifactId];
            var producer = graph.ById(producerId);

            if (producer.IteratesOver is { } producerList && step.IteratesOver != producerList)
            {
                foreach (var reference in artifacts.RequireAll(artifactId, iterationOrder(producerList)))
                {
                    inputs.Add(FileInput(paths, reference, producerId));
                }

                continue;
            }

            var scopedTarget = producer.IteratesOver is null ? null : target;
            inputs.Add(FileInput(paths, artifacts.Require(artifactId, scopedTarget), producerId));
        }

        foreach (var declared in step.ReadsVariables)
        {
            var name = ProjectState.BaseName(declared);
            var producerId = graph.VariableProducers[name];
            var producer = graph.ById(producerId);

            if (ProjectState.IsCollectionRead(declared))
            {
                foreach (var value in state.RequireAll(name, iterationOrder(producer.IteratesOver!)))
                {
                    inputs.Add(new InputRef(InputRef.VariableKind, value.Name, value.Hash, producerId));
                }

                continue;
            }

            var scopedName = ProjectState.Namespaced(name, producer.IteratesOver is null ? null : target);
            var single = state.Require(scopedName);
            inputs.Add(new InputRef(InputRef.VariableKind, single.Name, single.Hash, producerId));
        }

        return inputs;
    }

    /// <summary>The answer record as an input. Built in one place so the reconciler and the step
    /// that consumes the answer agree on its shape byte for byte.</summary>
    public static InputRef AnswerInput(ProjectPaths paths, string absoluteAnswerPath)
        => new(InputRef.FileKind, paths.Relative(absoluteAnswerPath), Canonical.HashFile(absoluteAnswerPath), OperatorProducer);

    /// <summary>True when a difference names an answer record, which feature 6.3 treats specially.</summary>
    public static bool IsAnswerRecord(InputDifference difference)
        => difference.Kind == InputRef.FileKind
           && Path.GetFileName(difference.Name).StartsWith(AnswerFilePrefix, StringComparison.Ordinal);

    private static InputRef FileInput(ProjectPaths paths, ArtifactRef reference, string producerId)
    {
        var absolute = paths.Absolute(reference.Path);
        if (!File.Exists(absolute))
        {
            throw new HaltException(
                $"A completion record names artifact '{reference.Path}', which is not on disk. " +
                "Artifact-before-record ordering exists precisely so this cannot happen (feature 2.4); " +
                "something removed the file after the record was written.");
        }

        return new InputRef(InputRef.FileKind, reference.Path, Canonical.HashFile(absolute), producerId);
    }

    /// <summary>
    /// Compares the inputs a record claims against the inputs the step would consume now.
    /// Returns null when they match, or the specific difference when they do not - the specific
    /// one, because feature 1.7 requires the invalidation record to name the input that differed
    /// and both hashes, not merely that something changed.
    /// </summary>
    public static InputDifference? FirstDifference(IReadOnlyList<InputRef> recorded, IReadOnlyList<InputRef> current)
    {
        var recordedByKey = recorded.ToDictionary(i => $"{i.Kind}:{i.Name}", StringComparer.Ordinal);
        var currentByKey = current.ToDictionary(i => $"{i.Kind}:{i.Name}", StringComparer.Ordinal);

        foreach (var input in current)
        {
            var key = $"{input.Kind}:{input.Name}";
            if (!recordedByKey.TryGetValue(key, out var was))
            {
                return new InputDifference(input.Name, input.Kind, "(not recorded as an input)", input.Hash);
            }

            if (was.Hash != input.Hash)
            {
                return new InputDifference(input.Name, input.Kind, was.Hash, input.Hash);
            }
        }

        foreach (var input in recorded)
        {
            if (!currentByKey.ContainsKey($"{input.Kind}:{input.Name}"))
            {
                return new InputDifference(input.Name, input.Kind, input.Hash, "(no longer an input)");
            }
        }

        return null;
    }
}

public sealed record InputDifference(string Name, string Kind, string ExpectedHash, string ActualHash);
