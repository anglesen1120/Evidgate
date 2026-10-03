using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>A case found in the tree, ready to register.</summary>
/// <param name="Path">Current repository path.</param>
/// <param name="Id">The durable id.</param>
/// <param name="Source"><c>annotation</c> or <c>sidecar</c>: the source recorded in <c>case_identity_source</c>.</param>
/// <param name="RawKey">What was read.</param>
/// <param name="Name">Display name (sidecar <c>name</c>, else the file's stem).</param>
/// <param name="Framework">Framework from the caller.</param>
/// <param name="Language">Language from the caller.</param>
/// <param name="Content">The file text, carried so the revision store can hash it; it is never stored.</param>
internal sealed record DiscoveredCase(string Path, Guid Id, string Source, string RawKey, string Name, string Framework, string? Language, string Content);

/// <summary>What a tree held, before it is matched against the database.</summary>
/// <param name="Cases">One row per file whose id was resolved, in path order.</param>
/// <param name="Problems">Warnings and source conflicts, in path order.</param>
/// <param name="HeldIds">Ids that appear in a conflict: their cases are neither registered nor marked missing.</param>
/// <param name="HeldDirectories">Directory prefixes (<c>dir/</c>, empty for the root) of sidecars that were refused or lost entries: the cases under them keep their state this sync.</param>
internal sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredCase> Cases,
    IReadOnlyList<CaseSyncEntry> Problems,
    IReadOnlySet<Guid> HeldIds,
    IReadOnlyList<string> HeldDirectories);

/// <summary>
/// Turns a tree snapshot into resolved cases (03.1.1-T1.2/T1.3, AC3–AC5): annotation and sidecar are equivalent
/// sources, both present and different is <c>id_source_conflict</c>, a malformed source is a warning for that file
/// only, and every other file is still processed. Pure: no database, no clock.
/// </summary>
internal sealed class CaseDiscovery(SidecarManifestReader sidecars)
{
    /// <summary>Problems kept in one report; a tree of broken files must not produce an unbounded answer.</summary>
    internal const int MaxProblems = 2000;

    /// <summary>Resolves <paramref name="files"/>.</summary>
    /// <param name="files">The tree.</param>
    public DiscoveryResult Discover(IReadOnlyList<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var problems = new List<CaseSyncEntry>();
        var held = new HashSet<Guid>();
        var heldDirectories = new HashSet<string>(StringComparer.Ordinal);

        var testFiles = new List<SourceFile>();
        var sidecarFiles = new List<SourceFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (!RepoPath.IsValid(file.Path))
            {
                problems.Add(Warning(file.Path, CaseSyncCodes.InvalidAnnotation, "the path is not a valid repository path"));
                continue;
            }

            if (!seenPaths.Add(file.Path))
            {
                problems.Add(Warning(file.Path, CaseSyncCodes.InvalidAnnotation, "the path appears twice in the tree"));
                continue;
            }

            (string.Equals(RepoPath.FileNameOf(file.Path), SidecarManifestReader.FileName, StringComparison.Ordinal)
                ? sidecarFiles
                : testFiles).Add(file);
        }

        // Sidecar entries by the file they describe. Two entries for one file are a conflict unless they agree.
        var fromSidecar = new Dictionary<string, SidecarEntry>(StringComparer.Ordinal);
        var sidecarConflicts = new HashSet<string>(StringComparer.Ordinal);
        var testPaths = testFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var sidecarFile in sidecarFiles)
        {
            var read = sidecars.Read(sidecarFile.Path, sidecarFile.Content);

            foreach (var problem in read.Problems)
            {
                problems.Add(Warning(problem.Path, CaseSyncCodes.InvalidSidecarEntry, problem.Message));
            }

            // A refused sidecar (or one that lost an entry) can no longer vouch for the files it used to identify:
            // fail safe and keep every case under its directory as it is.
            if (read.Problems.Count > 0)
            {
                var directory = RepoPath.DirectoryOf(sidecarFile.Path);
                heldDirectories.Add(directory.Length == 0 ? string.Empty : directory + "/");
            }

            foreach (var entry in read.Entries)
            {
                if (!testPaths.Contains(entry.FullPath))
                {
                    problems.Add(Warning(entry.FullPath, CaseSyncCodes.InvalidSidecarEntry, $"the sidecar {sidecarFile.Path} names a file that is not in the tree"));
                    continue;
                }

                if (fromSidecar.TryGetValue(entry.FullPath, out var earlier))
                {
                    if (earlier.Id != entry.Id)
                    {
                        sidecarConflicts.Add(entry.FullPath);
                        held.Add(earlier.Id);
                        held.Add(entry.Id);
                    }

                    continue;
                }

                fromSidecar[entry.FullPath] = entry;
            }
        }

        var resolved = new List<DiscoveredCase>();

        foreach (var file in testFiles)
        {
            if (file.Content.Length > AnnotationParser.MaxContentChars)
            {
                problems.Add(Warning(file.Path, CaseSyncCodes.InvalidAnnotation, "the file is larger than 2 MiB and was not scanned"));
                continue;
            }

            var annotation = AnnotationParser.Parse(file.Content);
            fromSidecar.TryGetValue(file.Path, out var sidecar);

            if (sidecarConflicts.Contains(file.Path))
            {
                problems.Add(Conflict(file.Path, CaseSyncCodes.IdSourceConflict, "two sidecar entries give this file different ids"));
                continue;
            }

            switch (annotation.Outcome)
            {
                case AnnotationOutcome.Multiple:
                    problems.Add(Conflict(file.Path, CaseSyncCodes.IdSourceConflict, "the file carries more than one evidgate.id annotation"));
                    HoldAnnotationIds(annotation.Raw, held);
                    if (sidecar is not null)
                    {
                        held.Add(sidecar.Id);
                    }

                    continue;
                case AnnotationOutcome.Invalid:
                    problems.Add(Warning(file.Path, CaseSyncCodes.InvalidAnnotation, "the evidgate.id annotation is not a UUID"));
                    continue;
                case AnnotationOutcome.Valid when sidecar is not null && sidecar.Id != annotation.Id:
                    problems.Add(Conflict(file.Path, CaseSyncCodes.IdSourceConflict, "the annotation and the sidecar give this file different ids"));
                    held.Add(annotation.Id!.Value);
                    held.Add(sidecar.Id);
                    continue;
                case AnnotationOutcome.Valid:
                    resolved.Add(Build(file, annotation.Id!.Value, "annotation", annotation.Raw!, sidecar?.Name));
                    break;
                default:
                    if (sidecar is not null)
                    {
                        resolved.Add(Build(file, sidecar.Id, "sidecar", sidecar.RawKey, sidecar.Name));
                    }

                    break;
            }
        }

        // The same id on two files of one commit: neither writes anything (AC2), and the id is held.
        var duplicates = resolved.GroupBy(found => found.Id).Where(group => group.Count() > 1).ToArray();
        var duplicateIds = duplicates.Select(group => group.Key).ToHashSet();
        foreach (var group in duplicates)
        {
            held.Add(group.Key);
            foreach (var found in group)
            {
                problems.Add(Conflict(found.Path, CaseSyncCodes.DuplicateCaseId, $"{group.Count()} files declare the id {group.Key:D}"));
            }
        }

        // An id that a conflicting source also claims is not registered from the clean file either.
        var keep = new List<DiscoveredCase>();
        foreach (var found in resolved.Where(found => !duplicateIds.Contains(found.Id)))
        {
            if (held.Contains(found.Id))
            {
                problems.Add(Conflict(found.Path, CaseSyncCodes.IdSourceConflict, $"the id {found.Id:D} is also claimed by a conflicting source"));
            }
            else
            {
                keep.Add(found);
            }
        }

        return new DiscoveryResult(
            [.. keep.OrderBy(found => found.Path, StringComparer.Ordinal)],
            [.. problems.OrderBy(problem => problem.Path, StringComparer.Ordinal).ThenBy(problem => problem.Code, StringComparer.Ordinal).Take(MaxProblems)],
            held,
            [.. heldDirectories.Order(StringComparer.Ordinal)]);
    }

    private static void HoldAnnotationIds(string? raw, HashSet<Guid> held)
    {
        foreach (var part in (raw ?? string.Empty).Split(','))
        {
            if (AnnotationParser.IsCanonicalUuid(part, out var id))
            {
                held.Add(id);
            }
        }
    }

    private static DiscoveredCase Build(SourceFile file, Guid id, string source, string rawKey, string? sidecarName)
    {
        var name = TextRules.IsClean(sidecarName) ? sidecarName! : Stem(file.Path);
        var framework = TextRules.IsClean(file.Framework) && !string.IsNullOrWhiteSpace(file.Framework)
            ? TextRules.Truncate(file.Framework, 64)
            : "unknown";
        var language = file.Language is { } l && TextRules.IsClean(l) && l.Length is > 0 and <= 64 ? l : null;

        return new DiscoveredCase(file.Path, id, source, rawKey, TextRules.Truncate(name, 512), framework, language, file.Content);
    }

    private static string Stem(string path)
    {
        var name = RepoPath.FileNameOf(path);
        var dot = name.LastIndexOf('.');

        return dot > 0 ? name[..dot] : name;
    }

    private static CaseSyncEntry Warning(string path, string code, string message) =>
        new(path, CaseSyncResults.Warning, null, code, message);

    private static CaseSyncEntry Conflict(string path, string code, string message) =>
        new(path, code, null, code, message);
}
