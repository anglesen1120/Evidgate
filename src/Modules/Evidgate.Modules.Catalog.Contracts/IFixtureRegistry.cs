namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// One fixture reference as a suite revision authors it: the fixture's key and the revision number it pins.
/// </summary>
/// <param name="Name">The fixture key (lower-case, dash-separated).</param>
/// <param name="Revision">The revision number, from 1.</param>
public readonly record struct FixtureRef(string Name, int Revision);

/// <summary>
/// What the suite write path asks the fixture side: which of the references it authored exist in this project at
/// exactly that revision. Must run inside the caller's tenant transaction, like the rest of the catalog surface.
/// </summary>
public interface IFixtureRegistry
{
    /// <summary>
    /// The members of <paramref name="references"/> that exist in <paramref name="projectId"/> at their own revision
    /// number. One statement for the whole list: the caller takes the difference and refuses what is missing, so a
    /// reference to another project, to a revision that does not exist (yet) and to a deleted fixture are one answer.
    /// </summary>
    /// <param name="projectId">The project the referencing suite belongs to.</param>
    /// <param name="references">The authored references; an empty list answers an empty set.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlySet<FixtureRef>> ExistsAsync(
        Guid projectId,
        IReadOnlyCollection<FixtureRef> references,
        CancellationToken cancellationToken);
}
