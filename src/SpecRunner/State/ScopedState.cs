using SpecRunner.Core;

namespace SpecRunner.State;

/// <summary>
/// Feature 1.1 - "Steps may not read anything they did not declare; enforce with a state
/// accessor that throws on an undeclared key rather than by convention."
///
/// This is that accessor. A step never sees <see cref="ProjectState"/> itself; it sees a view
/// narrowed to exactly the names it declared, and reaching outside that view throws rather than
/// returning a value. The dependency graph is therefore not a description of what steps
/// probably do - it is the only thing they are able to do.
/// </summary>
/// <param name="iterationOrderOf">
/// Resolves the frozen iteration order of the step that *produces* a given variable, which is
/// what a collection read ("name[]") iterates over. It is the producer's list, not the reader's:
/// the common case is a step that does not iterate at all gathering every value an upstream
/// iterating step committed. Null where collection reads are not permitted - inside a guard.
/// </param>
public sealed class ScopedState(
    ProjectState state,
    string stepId,
    IReadOnlyList<string> declaredReads,
    string? iterationTarget,
    Func<string, IReadOnlyList<string>>? iterationOrderOf)
{
    /// <summary>Reads a declared scalar variable, namespaced to the current iteration target.</summary>
    public string Get(string name)
    {
        RequireDeclared(name, collection: false);
        var scoped = ProjectState.Namespaced(name, iterationTarget);
        return state.Has(scoped) ? state.Require(scoped).Value : state.Require(name).Value;
    }

    /// <summary>
    /// Reads every value of an iterated variable, in the order of the frozen list its producing
    /// step iterated over. Declared as "name[]".
    /// </summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        RequireDeclared(name, collection: true);

        if (iterationOrderOf is null)
        {
            throw new HaltException(
                $"Step '{stepId}' attempted a collection read of '{name}[]' where collection reads are not " +
                "available. A guard runs before the frozen iteration lists it would need are resolved, so a guard " +
                "may only read single values.");
        }

        return [.. state.RequireAll(name, iterationOrderOf(name)).Select(v => v.Value)];
    }

    private void RequireDeclared(string name, bool collection)
    {
        var declaredForm = collection ? name + "[]" : name;
        foreach (var declared in declaredReads)
        {
            if (declared == declaredForm)
            {
                return;
            }
        }

        throw new HaltException(
            $"Step '{stepId}' read variable '{declaredForm}', which it did not declare. " +
            "Declarations are the dependency graph (feature 1.1); an undeclared read would make the graph a " +
            "description rather than a constraint, so it throws here instead of quietly succeeding. " +
            $"Declared reads are: {(declaredReads.Count == 0 ? "(none)" : string.Join(", ", declaredReads))}.");
    }
}
