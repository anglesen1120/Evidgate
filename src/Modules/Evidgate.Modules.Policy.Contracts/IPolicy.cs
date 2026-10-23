namespace Evidgate.Modules.Policy.Contracts;

/// <summary>
/// Policy's exported service interface (00 §5.5). 01.1.2-T2.1 placeholder: the
/// sprint-1 slice reaches Policy through the Mediator <c>GetDecision</c> query
/// (00 §5.5 C2); 07 fills this seam with the policy-revision/evaluation
/// interface other modules may call.
/// </summary>
public interface IPolicy
{
}
