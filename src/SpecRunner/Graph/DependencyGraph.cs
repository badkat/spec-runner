using System.Text;
using System.Text.RegularExpressions;
using SpecRunner.Core;
using SpecRunner.State;
using SpecRunner.Workflow;

namespace SpecRunner.Graph;

/// <summary>
/// Feature 1.1 / 1.2 - the static dependency graph, built without executing anything, and
/// validated before the web server binds.
///
/// A graph defect is a startup crash naming the offending ids, not a runtime surprise on step
/// 340. Everything the reconciler does - transitive invalidation especially (feature 1.6) - is
/// computed from this structure rather than stored anywhere.
/// </summary>
public sealed class DependencyGraph
{
    private static readonly Regex StepIdPattern = new(
        @"^phase-[0-9]+/task-[0-9]+/step-[0-9]+/[a-z0-9\-]+$", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _positionById;
    private readonly Dictionary<string, HashSet<string>> _downstream;

    private DependencyGraph(
        IReadOnlyList<Step> steps,
        Dictionary<string, int> positionById,
        Dictionary<string, HashSet<string>> downstream,
        IReadOnlyDictionary<string, string> variableProducers,
        IReadOnlyDictionary<string, string> artifactProducers)
    {
        Steps = steps;
        _positionById = positionById;
        _downstream = downstream;
        VariableProducers = variableProducers;
        ArtifactProducers = artifactProducers;
    }

    public IReadOnlyList<Step> Steps { get; }

    /// <summary>Variable name to the id of the single step that declares it as an output.</summary>
    public IReadOnlyDictionary<string, string> VariableProducers { get; }

    /// <summary>Artifact id to the id of the single step that declares it as an output.</summary>
    public IReadOnlyDictionary<string, string> ArtifactProducers { get; }

    public int PositionOf(string stepId) => _positionById[stepId];

    public Step ById(string stepId) => Steps[_positionById[stepId]];

    public bool Contains(string stepId) => _positionById.ContainsKey(stepId);

    /// <summary>
    /// Feature 1.6 - everything reachable downstream of a step in the declared graph.
    /// Invalidation is never local to one step, and this set is what makes that true. It is
    /// computed here, never stored: a stored reachability set is one more thing that can be
    /// stale, and Pillar 6 is precisely about not trusting stale derived work.
    /// </summary>
    public IReadOnlyList<string> DownstreamClosure(string stepId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(stepId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!_downstream.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var consumer in next)
            {
                if (seen.Add(consumer))
                {
                    queue.Enqueue(consumer);
                }
            }
        }

        return [.. seen.OrderBy(PositionOf)];
    }

    /// <summary>
    /// Builds and validates the graph. Every condition below is a startup crash with the
    /// offending ids attached; there is no partially-valid graph the application will run with.
    /// </summary>
    public static DependencyGraph Build(IReadOnlyList<Step> steps)
    {
        var problems = new List<string>();

        var positionById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            if (!StepIdPattern.IsMatch(step.Id))
            {
                problems.Add(
                    $"[{i}] step id '{step.Id}' does not match the required form " +
                    "'phase-<n>/task-<n>/step-<n>/<name>' (feature 1.3).");
            }

            if (!positionById.TryAdd(step.Id, i))
            {
                problems.Add($"[{i}] duplicate step id '{step.Id}' (first seen at position {positionById[step.Id]}).");
            }
        }

        var variableProducers = new Dictionary<string, string>(StringComparer.Ordinal);
        var artifactProducers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            foreach (var variable in step.WritesVariables)
            {
                if (ProjectState.IsCollectionRead(variable))
                {
                    problems.Add($"'{step.Id}' declares output variable '{variable}'; the '[]' suffix is a read form only.");
                    continue;
                }

                if (!variableProducers.TryAdd(variable, step.Id))
                {
                    problems.Add(
                        $"variable '{variable}' has two producers: '{variableProducers[variable]}' and '{step.Id}'. " +
                        "Exactly one producer per variable (feature 1.2).");
                }
            }

            foreach (var artifact in step.WritesArtifacts)
            {
                if (!artifactProducers.TryAdd(artifact, step.Id))
                {
                    problems.Add(
                        $"artifact '{artifact}' has two producers: '{artifactProducers[artifact]}' and '{step.Id}'. " +
                        "Exactly one producer per artifact (feature 1.2).");
                }
            }
        }

        var downstream = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Link(string producer, string consumer)
        {
            if (!downstream.TryGetValue(producer, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                downstream[producer] = set;
            }

            set.Add(consumer);
        }

        foreach (var step in steps)
        {
            foreach (var declared in step.ReadsVariables)
            {
                var name = ProjectState.BaseName(declared);
                if (!variableProducers.TryGetValue(name, out var producer))
                {
                    problems.Add($"'{step.Id}' reads variable '{name}', which no step declares as an output (feature 1.2).");
                    continue;
                }

                RequirePrecedes(problems, positionById, producer, step.Id, $"variable '{name}'");
                Link(producer, step.Id);

                // Feature 4.7 - variables are namespaced by iteration target. A consumer must
                // therefore agree with its producer about which iteration it is standing in, or
                // say explicitly that it wants the whole collection. Anything else is a name
                // that resolves to nothing at runtime, which is a graph defect and belongs here.
                if (!positionById.ContainsKey(producer))
                {
                    continue;
                }

                var producerStep = steps[positionById[producer]];
                var isCollection = ProjectState.IsCollectionRead(declared);

                // A guarded step may not run at all, so anything consuming what it produces must
                // be guarded too. Catching this at startup is the whole point of feature 1.2: an
                // ungated consumer of a conditional producer is a graph defect, and finding it on
                // step 340 instead of before the server binds would be the runtime surprise that
                // feature is written to prevent.
                if (producerStep.Guard is not null && step.Guard is null)
                {
                    problems.Add(
                        $"'{step.Id}' reads '{name}' from '{producer}', which is guarded and may not run. " +
                        "A consumer of a conditional producer must carry a guard of its own.");
                }

                if (producerStep.IteratesOver is null && isCollection)
                {
                    problems.Add(
                        $"'{step.Id}' reads '{declared}' as a collection, but '{producer}' does not iterate and " +
                        "commits a single value.");
                }
                else if (producerStep.IteratesOver is { } producerList && !isCollection
                         && step.IteratesOver != producerList)
                {
                    problems.Add(
                        $"'{step.Id}' reads '{name}' as a single value, but '{producer}' commits it once per item of " +
                        $"'{producerList}'. Either iterate over the same list, or declare the read as '{name}[]'.");
                }
            }

            foreach (var artifact in step.ReadsArtifacts)
            {
                if (!artifactProducers.TryGetValue(artifact, out var producer))
                {
                    problems.Add($"'{step.Id}' reads artifact '{artifact}', which no step declares as an output (feature 1.2).");
                    continue;
                }

                RequirePrecedes(problems, positionById, producer, step.Id, $"artifact '{artifact}'");
                Link(producer, step.Id);
            }

            if (step.IteratesOver is { } iterationSource)
            {
                if (!artifactProducers.TryGetValue(iterationSource, out var producer))
                {
                    problems.Add($"'{step.Id}' iterates over artifact '{iterationSource}', which no step declares as an output.");
                }
                else
                {
                    RequirePrecedes(problems, positionById, producer, step.Id, $"iteration source '{iterationSource}'");
                    Link(producer, step.Id);
                }

                if (!step.ReadsArtifacts.Contains(iterationSource))
                {
                    problems.Add(
                        $"'{step.Id}' iterates over '{iterationSource}' but does not declare it as a read artifact. " +
                        "The frozen list is an input like any other and must be hashed as one (feature 5.2).");
                }
            }

            if (step.Guard is { } guard)
            {
                foreach (var name in guard.ReadsVariables)
                {
                    if (!step.ReadsVariables.Contains(name))
                    {
                        problems.Add(
                            $"'{step.Id}' has a guard reading '{name}', which the step does not declare as a read. " +
                            "A guard is part of the step's dependency footprint, not an exception to it.");
                    }

                    // A guard decides whether the step applies at all, and it is evaluated before
                    // the frozen iteration lists a collection read would have to walk. Rejecting
                    // it here makes that a startup error rather than a surprise at runtime.
                    if (ProjectState.IsCollectionRead(name))
                    {
                        problems.Add(
                            $"'{step.Id}' has a guard reading the collection '{name}'. Guards may read single " +
                            "values only; move the collection read into the step body.");
                    }
                }
            }
        }

        // With "producer must precede consumer" enforced above, a cycle is not constructible.
        // Feature 1.2 names cycles explicitly, so the check is run rather than argued about.
        DetectCycles(steps, downstream, positionById, problems);

        if (problems.Count > 0)
        {
            var report = new StringBuilder("The workflow dependency graph is invalid. ");
            report.Append(problems.Count).Append(problems.Count == 1 ? " defect:" : " defects:");
            foreach (var problem in problems)
            {
                report.Append("\n  - ").Append(problem);
            }

            throw new HaltException(report.ToString());
        }

        return new DependencyGraph(steps, positionById, downstream, variableProducers, artifactProducers);
    }

    private static void RequirePrecedes(
        List<string> problems,
        Dictionary<string, int> positionById,
        string producer,
        string consumer,
        string what)
    {
        if (!positionById.TryGetValue(producer, out var producerPosition)
            || !positionById.TryGetValue(consumer, out var consumerPosition))
        {
            return;
        }

        if (producerPosition >= consumerPosition)
        {
            problems.Add(
                $"'{consumer}' consumes {what} produced by '{producer}', which does not precede it in sequence " +
                $"(producer at position {producerPosition}, consumer at position {consumerPosition}).");
        }
    }

    private static void DetectCycles(
        IReadOnlyList<Step> steps,
        Dictionary<string, HashSet<string>> downstream,
        Dictionary<string, int> positionById,
        List<string> problems)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new Stack<string>();

        foreach (var step in steps)
        {
            Visit(step.Id);
        }

        void Visit(string id)
        {
            if (!positionById.ContainsKey(id))
            {
                return;
            }

            if (state.TryGetValue(id, out var mark))
            {
                if (mark == 1)
                {
                    problems.Add($"cycle in the dependency graph through: {string.Join(" -> ", path.Reverse())} -> {id}.");
                }

                return;
            }

            state[id] = 1;
            path.Push(id);

            if (downstream.TryGetValue(id, out var next))
            {
                foreach (var consumer in next)
                {
                    Visit(consumer);
                }
            }

            path.Pop();
            state[id] = 2;
        }
    }
}
