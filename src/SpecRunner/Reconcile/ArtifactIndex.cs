using SpecRunner.Core;
using SpecRunner.Records;

namespace SpecRunner.Reconcile;

/// <summary>
/// Which artifact version is currently in force for each artifact id, and for each iteration
/// target where the producing step iterates.
///
/// Built by reconciliation from honored completion records and extended as steps commit during
/// the run. Never persisted: feature 1.13's rule that state is a projection applies to this too.
/// </summary>
public sealed class ArtifactIndex
{
    private readonly Dictionary<string, Dictionary<string, ArtifactRef>> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// The key standing for "no iteration target". A NUL cannot collide with a real target
    /// identity, and it is written as an escape rather than as a literal control character so
    /// the source stays pure ASCII — a raw NUL byte makes git treat the file as binary, and some
    /// editors strip it silently on save.
    /// </summary>
    private const string NoTargetKey = "\0";

    public void Put(ArtifactRef reference)
    {
        if (!_byId.TryGetValue(reference.ArtifactId, out var byTarget))
        {
            byTarget = new Dictionary<string, ArtifactRef>(StringComparer.Ordinal);
            _byId[reference.ArtifactId] = byTarget;
        }

        byTarget[reference.IterationTarget ?? NoTargetKey] = reference;
    }

    public bool Has(string artifactId, string? target)
        => _byId.TryGetValue(artifactId, out var byTarget) && byTarget.ContainsKey(target ?? NoTargetKey);

    public ArtifactRef Require(string artifactId, string? target)
    {
        if (_byId.TryGetValue(artifactId, out var byTarget)
            && byTarget.TryGetValue(target ?? NoTargetKey, out var reference))
        {
            return reference;
        }

        throw new HaltException(
            $"No in-force version of artifact '{artifactId}'" +
            (target is null ? "" : $" for iteration target '{target}'") +
            " is available. Its producing step has not committed one in this run and no honored record names one.");
    }

    /// <summary>Every in-force version of an artifact produced by an iterating step, in the given target order.</summary>
    public IReadOnlyList<ArtifactRef> RequireAll(string artifactId, IReadOnlyList<string> targetsInOrder)
        => [.. targetsInOrder.Select(t => Require(artifactId, t))];

    public IReadOnlyList<ArtifactRef> All()
        => [.. _byId.Values.SelectMany(v => v.Values).OrderBy(r => r.Path, StringComparer.Ordinal)];
}
