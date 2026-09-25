namespace Evidgate.Testing;

/// <summary>
/// Digest-pinned container images shared by every Testcontainers suite and by
/// infra/compose.dev.yml (01.8.1-T2, 01.6.1-T2). Digests are the multi-arch
/// image-index digests read from registry-1.docker.io on 2026-09-26.
/// </summary>
internal static class ContainerImages
{
    public const string Postgres =
        "postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722";

    public const string SeaweedFs =
        "chrislusf/seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882";
}
