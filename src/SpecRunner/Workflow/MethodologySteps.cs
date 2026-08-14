using System.Text;
using System.Text.Json;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Llm;

namespace SpecRunner.Workflow;

/// <summary>
/// Feature 1.12 - "the workflow's own earliest steps are responsible for creating the project's
/// starting files". An empty project directory is a valid startup state; this is what fills it.
///
/// The stub it writes is meant to be replaced by hand. When the operator does that, the artifact
/// no longer matches the body hash in its own origin header, and the next run classifies that as
/// a hand-edit rather than staleness (feature 1.8) - accepting what they wrote and invalidating
/// everything derived from the placeholder.
/// </summary>
public sealed class SeedBriefStep : Step
{
    public override string Id => "phase-1/task-1/step-1/seed-brief";

    public override string Description => "Create the project brief stub for the operator to fill in.";

    public override IReadOnlyList<string> WritesArtifacts => ["project-brief"];

    public override void Execute(StepContext context)
    {
        context.WriteArtifact("project-brief",
            """
            # Project brief

            Replace this stub with the actual brief, then answer the question the next step raises.

            Editing this file by hand is the expected way to use it. The next run will notice that
            the body no longer matches the hash recorded in this file's own origin header, report
            that as a hand-edit rather than as staleness, take what you wrote as truth, and
            invalidate everything that was derived from the stub.

            ## What the system is for

            _(replace)_

            ## Who uses it

            _(replace)_

            ## What it must not do

            _(replace)_
            """);

        context.SetSummary("Wrote the project brief stub. Nothing downstream is meaningful until it is replaced.");
    }
}

/// <summary>
/// A human decision over a code-defined closed enum (features 6.1, 6.2). The switch has no
/// fallthrough: `default` is a halt, because a value outside the enum means the record is
/// malformed and the application does not decide what the operator probably meant.
/// </summary>
public sealed class ConfirmBriefStep : Step
{
    public const string Ready = "brief-is-ready";
    public const string NotReady = "brief-is-not-ready";

    public override string Id => "phase-1/task-1/step-2/confirm-brief";

    public override string Description => "Ask whether the project brief is ready to derive requirements from.";

    public override bool RaisesDecision => true;

    public override IReadOnlyList<string> ReadsArtifacts => ["project-brief"];

    public override IReadOnlyList<string> WritesVariables => ["brief_confirmed"];

    public override void Execute(StepContext context)
    {
        var answer = context.AwaitDecision(
            "Is the project brief ready to derive requirements from?",
            [Ready, NotReady],
            Id,
            [context.ArtifactPath("project-brief")]);

        switch (answer)
        {
            case Ready:
                context.CommitVariable("brief_confirmed", Ready);
                context.SetSummary("The operator confirmed the brief is ready.");
                return;

            case NotReady:
                throw new HaltException(
                    "The operator answered that the project brief is not ready. The run stops here rather than " +
                    "deriving requirements from a brief that is known not to be finished (Pillar 8). Edit " +
                    $"{context.ArtifactPath("project-brief")}, then invalidate this step from the console or delete " +
                    "its answer record, and run again.")
                {
                    StepId = Id
                };

            default:
                throw new HaltException($"Answer '{answer}' is outside the closed enum for '{Id}'.") { StepId = Id };
        }
    }
}

/// <summary>The first model call. Everything about it comes from the template's front matter.</summary>
public sealed class DeriveRequirementsStep : Step
{
    public override string Id => "phase-1/task-2/step-1/derive-requirements";

    public override string Description => "Derive a numbered requirement list from the project brief.";

    public override string? PromptTemplate => "derive-requirements.md";

    public override IReadOnlyList<string> ReadsArtifacts => ["project-brief"];

    public override IReadOnlyList<string> ReadsVariables => ["brief_confirmed"];

    public override IReadOnlyList<string> WritesArtifacts => ["requirements"];

    public override IReadOnlyList<string> WritesVariables => ["requirement_items", "requirement_count"];

    public override void Execute(StepContext context)
    {
        // Reading brief_confirmed is what makes the human decision an actual dependency of this
        // step rather than a formality that happened earlier in the sequence.
        _ = context.State.Get("brief_confirmed");

        var call = context.CallModel(
            ("project_brief", context.ArtifactBody("project-brief"), "phase-1/task-1/step-1/seed-brief"));

        var parsed = context.ParseResponse(call);

        context.WriteArtifact("requirements", call.Result.Content, call);
        context.CommitVariable("requirement_items", parsed["items"]);
        context.CommitVariable("requirement_count", parsed["count"]);
        context.SetSummary($"Derived {parsed["count"]} requirement(s) from the project brief.");
    }
}

/// <summary>
/// Feature 5.1 - the iteration set is materialized to disk and frozen before the first item runs:
/// an ordered, numbered list artifact with its own origin header. Iteration proceeds against that
/// frozen list, never against a live re-scan.
/// </summary>
public sealed class FreezeRequirementSetStep : Step
{
    public override string Id => "phase-1/task-2/step-2/freeze-requirement-set";

    public override string Description => "Freeze the requirement list into an ordered, numbered iteration set.";

    public override IReadOnlyList<string> ReadsArtifacts => ["requirements"];

    public override IReadOnlyList<string> ReadsVariables => ["requirement_items"];

    public override IReadOnlyList<string> WritesArtifacts => ["requirement-set"];

    public override void Execute(StepContext context)
    {
        var texts = JsonSerializer.Deserialize<List<string>>(context.State.Get("requirement_items"))
            ?? throw new HaltException("The requirement_items variable did not deserialize to a list.");

        var sourcePath = context.ArtifactPath("requirements");
        var sourceHash = Canonical.HashFile(context.Paths.Absolute(sourcePath));

        var items = new List<IterationItem>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            items.Add(new IterationItem(i + 1, $"req-{i + 1:D3}", texts[i]));
        }

        context.WriteArtifact(
            "requirement-set",
            FrozenIterationList.RenderBody(items, sourcePath, sourceHash),
            extraHeader: doc => FrozenIterationList.Describe(doc, sourcePath, sourceHash, items));

        context.SetSummary($"Froze {items.Count} requirement(s) into an ordered iteration set.");
    }
}

/// <summary>
/// The iterating step. Feature 5.3 - the current iteration target is part of step identity in
/// records, so skippability is per-item and an interrupted iteration resumes at the exact item.
/// </summary>
public sealed class SpecifyRequirementStep : Step
{
    public override string Id => "phase-2/task-1/step-1/specify-requirement";

    public override string Description => "Write a specification for one requirement.";

    public override string? PromptTemplate => "specify-requirement.md";

    public override string? IteratesOver => "requirement-set";

    public override IReadOnlyList<string> ReadsArtifacts => ["requirement-set", "project-brief"];

    public override IReadOnlyList<string> WritesArtifacts => ["requirement-spec"];

    public override IReadOnlyList<string> WritesVariables => ["spec_title"];

    public override void Execute(StepContext context)
    {
        var target = context.IterationTarget
            ?? throw new HaltException($"'{Id}' iterates but was executed with no iteration target.");

        var list = FrozenIterationList.Read(context.Artifacts.Read(context.ArtifactPath("requirement-set")));
        var item = list.Items.FirstOrDefault(i => i.Identity == target)
            ?? throw new HaltException($"Iteration target '{target}' is not in the frozen list {list.ArtifactPath}.");

        var call = context.CallModel(
            ("requirement", $"{item.Identity}: {item.Text}", "phase-1/task-2/step-2/freeze-requirement-set"),
            ("project_brief", context.ArtifactBody("project-brief"), "phase-1/task-1/step-1/seed-brief"));

        var parsed = context.ParseResponse(call);

        context.WriteArtifact("requirement-spec", parsed["content"], call);
        context.CommitVariable("spec_title", item.Text);
        context.SetSummary($"Specified {item.Identity} ({item.Ordinal} of {list.Items.Count}).");
    }
}

/// <summary>
/// Feature 7.1 - a downstream validator emitting a machine-checkable verdict field. It writes the
/// verdict artifact; it does not decide anything. The step after this one decides, by reading the
/// enum and nothing else.
/// </summary>
public sealed class ValidateSpecsStep : Step
{
    public override string Id => "phase-2/task-2/step-1/validate-specs";

    public override string Description => "Check the specifications against the requirements they came from.";

    public override string? PromptTemplate => "validate-specs.md";

    public override IReadOnlyList<string> ReadsArtifacts => ["requirements", "requirement-spec"];

    public override IReadOnlyList<string> ReadsVariables => ["spec_title[]"];

    public override IReadOnlyList<string> WritesArtifacts => ["spec-validation"];

    public override IReadOnlyList<string> WritesVariables => ["validation_verdict", "suspected_artifact"];

    public override void Execute(StepContext context)
    {
        var titles = context.State.GetAll("spec_title");
        var specs = new StringBuilder();
        var index = 0;

        foreach (var (target, body) in context.AllArtifactBodies("requirement-spec"))
        {
            specs.Append("## ").Append(target).Append(" — ").Append(titles[index++]).Append("\n\n")
                 .Append(body).Append("\n\n");
        }

        var call = context.CallModel(
            ("requirements", context.ArtifactBody("requirements"), "phase-1/task-2/step-1/derive-requirements"),
            ("specifications", specs.ToString(), Id));

        var parsed = context.ParseResponse(call);

        context.WriteArtifact("spec-validation", call.Result.Content, call);
        context.CommitVariable("validation_verdict", parsed["verdict"]);
        context.CommitVariable("suspected_artifact", parsed["suspected_artifact"]);
        context.SetSummary($"Validation verdict: {parsed["verdict"]}.");
    }
}

/// <summary>
/// Pillar 4 in one class. The predicate is a switch over the closed verdict enum; the prose the
/// validator wrote is never read here. Feature 2.8 - the branch itself lands on disk, with the
/// exact input value it evaluated, so a developer holding only the code and the stored inputs can
/// re-derive it.
/// </summary>
public sealed class RouteVerdictStep : Step
{
    public const string Accepted = "accepted";
    public const string DefectSuspected = "defect-suspected";

    /// <summary>
    /// The closed set of upstream artifacts a defect finding is permitted to name. The validator
    /// produces a string; this list is what makes that string routable. An id outside it is a
    /// halt, so the model can never point the cascade at something the code did not sanction.
    /// </summary>
    public static readonly IReadOnlyList<string> RoutableUpstreamArtifacts = ["requirements"];

    public override string Id => "phase-2/task-2/step-2/route-verdict";

    public override string Description => "Branch on the validation verdict.";

    public override IReadOnlyList<string> ReadsVariables => ["validation_verdict", "suspected_artifact"];

    public override IReadOnlyList<string> WritesVariables => ["validation_outcome"];

    public override void Execute(StepContext context)
    {
        var verdict = context.State.Get("validation_verdict");

        var outcome = verdict switch
        {
            OutputParsers.VerdictPass => Accepted,
            OutputParsers.VerdictUpstreamDefectSuspected => DefectSuspected,
            _ => throw new HaltException(
                $"Verdict '{verdict}' is outside the closed enum ({string.Join(", ", OutputParsers.Verdicts)}). " +
                "There is no default branch here (feature 6.2 applies to every switch over a closed set, not only " +
                "to answers).")
            { StepId = Id }
        };

        if (outcome == DefectSuspected)
        {
            var suspected = context.State.Get("suspected_artifact");
            if (!RoutableUpstreamArtifacts.Contains(suspected))
            {
                throw new HaltException(
                    $"The validator named '{suspected}' as the suspected upstream artifact, which is not in the " +
                    $"code-defined routable set ({string.Join(", ", RoutableUpstreamArtifacts)}). The model does not " +
                    "get to choose what the invalidation cascade points at (Pillar 4).")
                {
                    StepId = Id
                };
            }
        }

        context.RecordBranch(
            predicateIdentity: $"{Id}: switch on validation_verdict",
            evaluatedInput: "validation_verdict",
            evaluatedValue: verdict,
            options: OutputParsers.Verdicts,
            chosenBranch: outcome,
            nextStep: outcome == Accepted
                ? "phase-3/task-1/step-1/final-report"
                : "phase-2/task-2/step-3/raise-defect-finding",
            explanation:
            "Code read the verdict field and nothing else. The validator's prose was not consulted, and no part " +
            "of this decision appeals to the model's judgment, intent, or tone.");

        context.CommitVariable("validation_outcome", outcome);
        context.SetSummary($"Verdict '{verdict}' routed to '{outcome}'.");
    }
}

/// <summary>
/// Feature 7.2 - a suspected upstream defect produces a defect-finding artifact and halts for
/// confirmation. The finding names the suspected upstream artifact and version, the evidence, and
/// the downstream step that raised it.
/// </summary>
public sealed class RaiseDefectFindingStep : Step
{
    public override string Id => "phase-2/task-2/step-3/raise-defect-finding";

    public override string Description => "Write a defect finding naming the suspected upstream artifact.";

    public override StepGuard? Guard => new(
        "the validation verdict suspected an upstream defect",
        ["validation_outcome"],
        state => state.Get("validation_outcome") == RouteVerdictStep.DefectSuspected);

    public override IReadOnlyList<string> ReadsArtifacts => ["spec-validation"];

    public override IReadOnlyList<string> ReadsVariables => ["validation_outcome", "suspected_artifact"];

    public override IReadOnlyList<string> WritesArtifacts => ["defect-finding"];

    public override IReadOnlyList<string> WritesVariables => ["defect_finding_upstream_step"];

    public override void Execute(StepContext context)
    {
        var suspected = context.State.Get("suspected_artifact");
        var upstreamStep = context.ProducerOf(suspected);
        var validationPath = context.ArtifactPath("spec-validation");

        context.WriteArtifact("defect-finding",
            $"""
             # Defect finding

             - Suspected upstream artifact: `{suspected}`
             - Produced by step: `{upstreamStep}`
             - Raised by downstream step: `phase-2/task-2/step-1/validate-specs`
             - Evidence: `{validationPath}`

             ## What happens next

             Backward flow does not begin until a person confirms this finding. The application never
             rewinds unattended.

             Confirming invalidates `{upstreamStep}` and everything reachable downstream of it, through
             the same invalidation cascade as any other invalidation — there is no separate rewind
             engine. Rejecting leaves the chain intact and the run continues to its report.

             ## The validator's evidence, verbatim

             {context.ArtifactBody("spec-validation")}
             """);

        context.CommitVariable("defect_finding_upstream_step", upstreamStep);
        context.SetSummary($"Raised a defect finding against '{suspected}', produced by '{upstreamStep}'.");
    }
}

/// <summary>The confirmation itself. Backward flow does not begin until a person says so.</summary>
public sealed class ConfirmDefectStep : Step
{
    public const string Confirm = "confirm-upstream-defect";
    public const string Reject = "reject-finding";

    public override string Id => "phase-2/task-2/step-4/confirm-defect";

    public override string Description => "Ask whether the suspected upstream defect is real.";

    public override bool RaisesDecision => true;

    public override StepGuard? Guard => new(
        "a defect finding was raised",
        ["validation_outcome"],
        state => state.Get("validation_outcome") == RouteVerdictStep.DefectSuspected);

    public override IReadOnlyList<string> ReadsArtifacts => ["defect-finding"];

    public override IReadOnlyList<string> ReadsVariables => ["validation_outcome", "defect_finding_upstream_step"];

    public override IReadOnlyList<string> WritesVariables => ["defect_decision"];

    public override void Execute(StepContext context)
    {
        var upstreamStep = context.State.Get("defect_finding_upstream_step");

        var answer = context.AwaitDecision(
            $"A downstream validator suspects a defect in '{upstreamStep}'. Confirm, and invalidate it along with "
            + "everything derived from it?",
            [Confirm, Reject],
            Id,
            [context.ArtifactPath("defect-finding")]);

        context.CommitVariable("defect_decision", answer);
        context.SetSummary($"The operator answered '{answer}'.");
    }
}

/// <summary>
/// Feature 7.3 - confirming a defect invalidates the named upstream artifact and everything
/// derived from it through the same code path as any other invalidation. This step contains no
/// rewind logic of its own; it calls the cascade and then stops, because workflow position is
/// derived and the way to act on an invalidation is to run again.
/// </summary>
public sealed class ApplyBackwardFlowStep : Step
{
    public override string Id => "phase-2/task-2/step-5/apply-backward-flow";

    public override string Description => "Hand a confirmed defect to the invalidation cascade.";

    public override StepGuard? Guard => new(
        "the operator confirmed the upstream defect",
        ["validation_outcome", "defect_decision"],
        state => state.Get("validation_outcome") == RouteVerdictStep.DefectSuspected
                 && state.Get("defect_decision") == ConfirmDefectStep.Confirm);

    public override IReadOnlyList<string> ReadsArtifacts => ["defect-finding"];

    public override IReadOnlyList<string> ReadsVariables =>
        ["validation_outcome", "defect_decision", "defect_finding_upstream_step"];

    public override void Execute(StepContext context)
    {
        var upstreamStep = context.State.Get("defect_finding_upstream_step");
        var findingPath = context.ArtifactPath("defect-finding");

        context.Note(
            EventKinds.DefectFinding,
            $"Confirmed defect in '{upstreamStep}'. Applying the invalidation cascade.",
            Emit.Fields("upstream_step", upstreamStep, "finding", findingPath));

        context.ConfirmDefect(upstreamStep, "phase-2/task-2/step-1/validate-specs", findingPath);

        throw new HaltException(
            $"Backward flow applied: '{upstreamStep}' and everything derived from it are invalidated. " +
            "Correct the upstream source, then run again - replay will skip what is still in force and re-establish " +
            "the chain from the corrected source.")
        {
            StepId = Id
        };
    }
}

/// <summary>The account of what this run established. No model call; just the record.</summary>
public sealed class FinalReportStep : Step
{
    public override string Id => "phase-3/task-1/step-1/final-report";

    public override string Description => "Write the report for this pass of the workflow.";

    public override IReadOnlyList<string> ReadsArtifacts => ["requirements", "requirement-spec", "spec-validation"];

    public override IReadOnlyList<string> ReadsVariables =>
        ["requirement_count", "validation_outcome", "spec_title[]"];

    public override IReadOnlyList<string> WritesArtifacts => ["run-report"];

    public override void Execute(StepContext context)
    {
        var titles = context.State.GetAll("spec_title");
        var specs = context.AllArtifactBodies("requirement-spec");

        var body = new StringBuilder();
        body.Append("# Run report\n\n")
            .Append($"- Requirements derived: {context.State.Get("requirement_count")}\n")
            .Append($"- Specifications written: {specs.Count}\n")
            .Append($"- Validation outcome: {context.State.Get("validation_outcome")}\n")
            .Append($"- Requirements artifact: `{context.ArtifactPath("requirements")}`\n")
            .Append($"- Validation artifact: `{context.ArtifactPath("spec-validation")}`\n\n")
            .Append("## Specifications\n\n");

        for (var i = 0; i < specs.Count; i++)
        {
            body.Append($"- `{specs[i].Target}` — {titles[i]}\n");
        }

        context.WriteArtifact("run-report", body.ToString());
        context.SetSummary($"Reported {specs.Count} specification(s) with outcome '{context.State.Get("validation_outcome")}'.");
    }
}
