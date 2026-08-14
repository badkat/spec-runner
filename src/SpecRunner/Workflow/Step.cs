using SpecRunner.State;

namespace SpecRunner.Workflow;

/// <summary>
/// A code-defined condition deciding whether a step applies at all. Pillar 4 - the predicate is
/// code evaluating a value code chose to look at; nothing about it appeals to a model's judgment.
/// The variables it reads are declared so the dependency graph accounts for them (feature 1.1).
/// </summary>
public sealed record StepGuard(
    string Description,
    IReadOnlyList<string> ReadsVariables,
    Func<ScopedState, bool> Predicate);

/// <summary>
/// The atomic unit of the methodology. Everything a step touches is declared here, as static
/// metadata alongside its code, so the dependency graph is constructible at startup from
/// declarations alone - without executing anything (feature 1.1).
///
/// Steps are of two kinds, per the execution model: pure steps leave no record and simply re-run
/// on replay; committing steps leave a record on disk, and the presence of that record is what
/// makes them skippable. That distinction is not a flag on this class - it falls out of whether
/// the step commits anything, which the engine observes rather than the step declares.
/// </summary>
public abstract class Step
{
    /// <summary>
    /// Feature 1.3 - an explicit string constant of the form
    /// <c>phase-2/task-3/step-4/name</c>. Never derived from method names, class names,
    /// reflection order, or file position. Records are keyed by this, so renaming one is a
    /// deliberate act that invalidates the step and its descendants.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>One line, for the pre-flight plan and the step detail view.</summary>
    public abstract string Description { get; }

    public virtual IReadOnlyList<string> ReadsVariables => [];

    public virtual IReadOnlyList<string> WritesVariables => [];

    /// <summary>Artifact ids this step reads. An id, not a path - versions are resolved at runtime.</summary>
    public virtual IReadOnlyList<string> ReadsArtifacts => [];

    public virtual IReadOnlyList<string> WritesArtifacts => [];

    /// <summary>
    /// Prompt template path relative to the prompts directory, when this step calls a model. The
    /// template file's hash is one of the step's declared inputs, so editing a template
    /// invalidates the step (feature 4.3).
    /// </summary>
    public virtual string? PromptTemplate => null;

    /// <summary>
    /// The artifact id of a frozen iteration list (feature 5.1) this step iterates over. The
    /// engine runs the step once per item, with the item's identity forming part of the record
    /// key (feature 5.3), so an interrupted iteration resumes at the exact item.
    /// </summary>
    public virtual string? IteratesOver => null;

    public virtual StepGuard? Guard => null;

    /// <summary>
    /// True when this step blocks for a human decision. The answer record is then one of the
    /// step's inputs, hashed like any other (feature 1.4), which is what lets feature 6.3 notice
    /// that someone edited an answer that is already in force and halt on the conflict instead of
    /// quietly acting on the new value - or, worse, quietly not acting on it.
    /// </summary>
    public virtual bool RaisesDecision => false;

    public abstract void Execute(StepContext context);
}
