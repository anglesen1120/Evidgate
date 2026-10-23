namespace Evidgate.BuildingBlocks.SharedKernel.Messaging;

/// <summary>
/// A command that must be idempotent under the client's <c>Idempotency-Key</c>
/// (00 §9.1, TL T11): the C2 idempotency behaviour claims the key in the command's
/// tenant scope before the handler runs, replays the stored response for a retry
/// carrying the same request, and answers 409 <c>idempotency_conflict</c> for a
/// different one. The marker deliberately lives beside the Result contract rather
/// than with the persistence store: it is part of the command's declared shape.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>The client-supplied <c>Idempotency-Key</c> (00 §9.1); at most 128 characters (00 §7).</summary>
    string IdempotencyKey { get; }

    /// <summary>
    /// The dedupe operation the key is claimed for. Defaults to the command's full
    /// type name, so a key never crosses command types; a command overrides it only
    /// to keep one operation name stable across a rename.
    /// </summary>
    string Operation => GetType().FullName!;
}
