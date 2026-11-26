namespace Evidgate.Modules.Authoring.Application.Intake.Jobs;

/// <summary>The payload of an extraction task (<c>{"intake_id": ...}</c>).</summary>
/// <param name="IntakeId">The received intake to extract.</param>
internal sealed record ExtractIntakePayload(Guid IntakeId);
