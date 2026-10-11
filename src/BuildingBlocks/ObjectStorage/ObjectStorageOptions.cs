namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// Object-storage options, bound from <c>EVIDGATE__OBJECT__*</c> (06.1.2-T1). The
/// endpoint is optional: empty means the AWS regional endpoint derived from
/// <see cref="Region"/>, which is what a real provider uses; a local emulator
/// (SeaweedFS) sets it to its own absolute http(s) URL.
/// </summary>
/// <remarks>
/// The two credentials (<see cref="AccessKey"/>, <see cref="SecretKey"/>) have
/// internal setters, so the configuration binder skips them; they are resolved
/// through <c>ISecretProvider</c> by <see cref="ObjectStorageOptionsSecretPostConfigure"/>
/// (10.1.1-T2). Validation is lazy — it runs when <c>IObjectStorage</c> is first
/// resolved, not at host start — so a host that never touches object storage needs
/// no configuration.
/// </remarks>
public sealed class ObjectStorageOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__OBJECT__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Object";

    /// <summary>Absolute http(s) S3 endpoint; empty selects the AWS regional endpoint of <see cref="Region"/>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>SigV4 signing region, e.g. <c>us-east-1</c>.</summary>
    public string? Region { get; set; }

    /// <summary>Bucket the staging keys live in.</summary>
    public string? Bucket { get; set; }

    /// <summary>Presigned-URL lifetime in seconds (00 §9.3: at most 15 minutes).</summary>
    public int PresignExpirySeconds { get; set; } = 900;

    /// <summary>Path-style addressing; every S3-compatible emulator (SeaweedFS) needs it.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Access key of the object-storage identity, resolved through <c>ISecretProvider</c>.</summary>
    public string? AccessKey { get; internal set; }

    /// <summary>Secret key of the object-storage identity, resolved through <c>ISecretProvider</c>.</summary>
    public string? SecretKey { get; internal set; }
}
