using SpecRunner.Core;

namespace SpecRunner.State;

/// <summary>One committed variable, with the step that produced it.</summary>
public sealed record VariableValue(string Name, string Value, string Hash, string ProducerStepId);

/// <summary>
/// The workflow's variables. Not a store - a projection.
///
/// Feature 1.13 - state is always reconstructed from artifacts and records at startup, and the
/// on-disk state file is a convenience for human reading, never an input to execution. This
/// object is that reconstruction, held in memory for the duration of a run and rebuilt from
/// scratch the next time.
/// </summary>
public sealed class ProjectState
{
    private readonly Dictionary<string, VariableValue> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, VariableValue> Values => _values;

    /// <summary>
    /// Feature 4.7 - variables are single-assignment within a run. Writing a variable that
    /// already holds a value is a halt unless the step is re-executing after invalidation, which
    /// the caller signals with <paramref name="reExecuting"/>. Iteration cannot collide with
    /// itself because names are namespaced by target before they reach here.
    /// </summary>
    public void Commit(string name, string value, string producerStepId, bool reExecuting)
    {
        if (_values.TryGetValue(name, out var existing) && !reExecuting)
        {
            throw new HaltException(
                $"Variable '{name}' already holds a value committed by '{existing.ProducerStepId}'; " +
                $"'{producerStepId}' is attempting to write it again. Variables are single-assignment " +
                "within a run (feature 4.7). If this is a legitimate re-execution, the step's record " +
                "must have been invalidated first.");
        }

        _values[name] = new VariableValue(name, value, Canonical.HashValue(value), producerStepId);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public VariableValue Require(string name)
    {
        if (_values.TryGetValue(name, out var value))
        {
            return value;
        }

        throw new HaltException(
            $"Variable '{name}' has no committed value. Its producing step either has not run or did not " +
            "commit what it declared - either way this is a defect, not a condition to default around.");
    }

    /// <summary>
    /// All values of an iterated variable, in the order of the frozen iteration list. Used for
    /// a declared collection read (a name ending in "[]").
    /// </summary>
    public IReadOnlyList<VariableValue> RequireAll(string baseName, IReadOnlyList<string> targetsInOrder)
    {
        var collected = new List<VariableValue>(targetsInOrder.Count);
        foreach (var target in targetsInOrder)
        {
            collected.Add(Require(Namespaced(baseName, target)));
        }

        return collected;
    }

    /// <summary>Feature 4.7 - the namespacing that keeps an iterating step from colliding with itself.</summary>
    public static string Namespaced(string name, string? iterationTarget)
        => iterationTarget is null ? name : $"{name}@{iterationTarget}";

    public static string BaseName(string declaredName)
        => declaredName.EndsWith("[]", StringComparison.Ordinal) ? declaredName[..^2] : declaredName;

    public static bool IsCollectionRead(string declaredName)
        => declaredName.EndsWith("[]", StringComparison.Ordinal);
}
