using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// Resolves the object-storage credentials through <see cref="ISecretProvider"/>
/// after the binder has run (06.1.2-T1, 10.1.1-T2). The two properties have
/// internal setters, so <c>BindConfiguration</c> skips them and this is their only
/// writer; validation runs after post-configuration, so
/// <see cref="ObjectStorageOptionsValidator"/> still sees the resolved values.
/// </summary>
internal sealed class ObjectStorageOptionsSecretPostConfigure : IPostConfigureOptions<ObjectStorageOptions>
{
    private readonly ISecretProvider _secrets;

    public ObjectStorageOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AccessKey = _secrets.GetSecret(SecretNames.ObjectAccessKey);
        options.SecretKey = _secrets.GetSecret(SecretNames.ObjectSecretKey);
    }
}
