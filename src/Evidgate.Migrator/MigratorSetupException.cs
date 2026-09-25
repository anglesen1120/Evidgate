namespace Evidgate.Migrator;

/// <summary>
/// The Migrator cannot start: the invocation, the configuration, the migrations
/// directory or the migration set itself is unusable. Maps to
/// <see cref="MigratorExitCodes.InvalidInvocation"/>.
/// </summary>
public sealed class MigratorSetupException : Exception
{
    public MigratorSetupException(string error)
        : this([error])
    {
    }

    public MigratorSetupException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }

    public MigratorSetupException(IReadOnlyList<string> errors, Exception innerException)
        : base(string.Join("; ", errors), innerException)
    {
        Errors = errors;
    }

    /// <summary>One entry per problem found, each naming the file or setting at fault.</summary>
    public IReadOnlyList<string> Errors { get; }
}
