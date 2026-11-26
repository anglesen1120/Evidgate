using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Application.Intake.Jobs;
using Evidgate.Modules.Evidence.Contracts;
using Npgsql;

namespace Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;

/// <summary>
/// Extracts the text of one received document intake. The row and the tenant's slot are claimed in one short
/// transaction, the object is fetched once and parsed with no transaction open, and the terminal state is written in a
/// second short one. Every refusal is a closed failure code on the row, never an exception: a cap read from the row,
/// an artifact the policy no longer allows, a missing object and a deadline all end as <c>rejected</c>.
/// </summary>
/// <remarks>
/// The parser is synchronous and cannot be killed, so at the deadline it is abandoned, not stopped. The tenant's slot
/// is therefore not released with the terminal write: it stays charged, its lease renewed, until the parser has really
/// exited. That keeps the per-tenant bound a bound on running parsers, not on attempts.
/// </remarks>
internal sealed class ExtractIntakeJob(
    IntakeExtractionStore store,
    IIntakeArtifactReader artifacts,
    DocumentTextExtractor extractor,
    TimeProvider time,
    IntakeExecutionPermits permits)
{
    /// <summary>The slot step of a renewal, as counted when it fails.</summary>
    internal const string RenewStep = "renew";

    /// <summary>The slot step of a release, as counted when it fails.</summary>
    internal const string ReleaseStep = "release";

    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(10);

    // Well inside the lease margin, so one failed renewal does not let the lease lapse under a running parser.
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Task, byte> _holds = new();

    /// <summary>Completes when every slot held for an abandoned parser has been given back.</summary>
    internal Task WhenSlotsReleasedAsync() => Task.WhenAll(_holds.Keys);

    /// <summary>Runs one attempt; a terminal or absent intake is a no-op and writes nothing.</summary>
    /// <param name="intakeId">The intake.</param>
    /// <param name="orgId">The tenant of the claimed task row.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    internal async Task RunAsync(Guid intakeId, Guid orgId, CancellationToken cancellationToken)
    {
        // Before the claim, so a Worker with every parser permit taken changes nothing: the row stays as it is and the
        // task is deferred, not retried.
        if (!permits.TryEnter())
        {
            throw new TaskDeferredException(IntakeExtractionStore.SlotWait);
        }

        var parse = new ParseRun();
        try
        {
            await RunPermittedAsync(intakeId, orgId, parse, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!parse.OwnsPermit)
            {
                permits.Exit();
            }
        }
    }

    private async Task RunPermittedAsync(Guid intakeId, Guid orgId, ParseRun parse, CancellationToken cancellationToken)
    {
        var claim = await store.ClaimAsync(orgId, intakeId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return;
        }

        IntakeJobMetrics.Record(claim.Kind, "extracting");

        var finished = false;
        var slotKept = false;

        try
        {
            var started = time.GetTimestamp();
            var outcome = await ExtractAsync(claim, parse, cancellationToken).ConfigureAwait(false);
            IntakeJobMetrics.RecordDuration(claim.Kind, time.GetElapsedTime(started));

            slotKept = parse.Running is { IsCompleted: false };
            var applied = await store.FinishAsync(orgId, claim, outcome, !slotKept, cancellationToken).ConfigureAwait(false);
            finished = true;

            if (applied)
            {
                IntakeJobMetrics.Record(claim.Kind, outcome.Succeeded ? "ready" : "rejected", outcome.FailureCode);
            }
        }
        finally
        {
            if (parse.Running is { IsCompleted: false } running)
            {
                HoldSlot(orgId, claim, running);
            }
            else if (!finished || slotKept)
            {
                // Not the attempt's own token: it is the one that was cancelled. The slot goes back at once so the
                // tenant's next intake is not held for a lease term; the row stays extracting for the next attempt.
                using var budget = new CancellationTokenSource(ReleaseBudget);
                await store.ReleaseAsync(orgId, claim, budget.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<ExtractionOutcome> ExtractAsync(IntakeClaim claim, ParseRun parse, CancellationToken cancellationToken)
    {
        if (Refusal(claim) is { } refusal)
        {
            return ExtractionOutcome.Refused(refusal);
        }

        var caps = claim.Caps!;
        var artifact = claim.Artifact!;

        // The deadline covers the fetch and the parse; the caller's own cancellation stays distinguishable from it.
        using var timeout = new CancellationTokenSource(caps.ExtractionTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            byte[]? content;
            try
            {
                content = await artifacts
                    .FetchAsync(artifact, Math.Min(caps.MaxDocumentBytes, artifact.MaxBytes), deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
            {
                return ExtractionOutcome.Refused(IntakeFailureCode.ArchiveLimitExceeded);
            }

            if (content is null || content.LongLength != claim.DeclaredBytes)
            {
                return ExtractionOutcome.Refused(IntakeFailureCode.ExtractionFailed);
            }

            var token = deadline.Token;

            // Task.Run only honours the token before the work starts, and the parser is synchronous: WaitAsync is what
            // returns at the deadline. A stalled parse is abandoned (its thread is not killed); the extractor refuses any
            // result that arrives after its own deadline, so the abandoned run can never be taken as a success.
            // The permit goes to the parser: it is given back when the parser exits, which for an abandoned parser is
            // long after this attempt stopped waiting.
            parse.OwnsPermit = true;
            var parser = Task.Run(
                () =>
                {
                    try
                    {
                        using var stream = new MemoryStream(content, writable: false);
                        return extractor.TryExtract(stream, claim.Kind, claim.MediaType, caps, token);
                    }
                    finally
                    {
                        permits.Exit();
                    }
                },
                CancellationToken.None);
            parse.Running = parser;

            return await parser.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExtractionOutcome.Refused(IntakeFailureCode.ExtractionTimeout);
        }
    }

    /// <summary>The refusal that needs no fetch, or null when the bytes may be read.</summary>
    private static string? Refusal(IntakeClaim claim)
    {
        if (claim.Caps is not { } caps
            || claim.Kind is not ("docx" or "pdf")
            || claim.Artifact is not { } artifact
            || !string.Equals(artifact.Kind, ArtifactKinds.RequirementDocument, StringComparison.Ordinal)
            || !artifact.MediaAllowed
            || artifact.DeclaredBytes != claim.DeclaredBytes)
        {
            return IntakeFailureCode.ExtractionFailed;
        }

        if (!MediaTypes.Equivalent(artifact.MediaType, claim.MediaType))
        {
            return IntakeFailureCode.ContainerMismatch;
        }

        return claim.DeclaredBytes > Math.Min(caps.MaxDocumentBytes, artifact.MaxBytes)
            ? IntakeFailureCode.ArchiveLimitExceeded
            : null;
    }

    private void HoldSlot(Guid orgId, IntakeClaim claim, Task parser)
    {
        var hold = HoldAsync(orgId, claim, parser);
        _holds.TryAdd(hold, 0);
        _ = hold.ContinueWith(done => _holds.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>Keeps the slot's lease alive while the abandoned parser runs, then gives the slot back.</summary>
    private async Task HoldAsync(Guid orgId, IntakeClaim claim, Task parser)
    {
#pragma warning disable VSTHRD003 // The parser task is the one this attempt started with Task.Run; it is awaited only to learn when it exits.
        try
        {
            while (!parser.IsCompleted)
            {
                using var tick = new CancellationTokenSource();
                var delay = Task.Delay(RenewEvery, time, tick.Token);
                var first = await Task.WhenAny(parser, delay).ConfigureAwait(false);
                await tick.CancelAsync().ConfigureAwait(false);

                if (first != parser)
                {
                    await QuietAsync(RenewStep, token => store.RenewAsync(orgId, claim, token)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                await parser.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only the exit matters here: the outcome was written when the attempt gave up on the parser.
            }

            await QuietAsync(ReleaseStep, token => store.ReleaseAsync(orgId, claim, token)).ConfigureAwait(false);
        }
#pragma warning restore VSTHRD003
    }

    // A failed renewal or release is not an error of the intake: the lease margin covers the next tick, and an expired
    // lease frees the slot at the next claim. It is counted, though, so a grant gap or a dead database shows as a
    // series instead of as slots that silently stay held until their lease lapses.
    internal static async Task QuietAsync(string operation, Func<CancellationToken, Task> step)
    {
        try
        {
            using var budget = new CancellationTokenSource(ReleaseBudget);
            await step(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IntakeJobMetrics.RecordSlotFailure(
                operation,
                exception is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege });
        }
    }

    /// <summary>The parser task of one attempt, set when it starts, so the attempt can tell whether it still runs.</summary>
    private sealed class ParseRun
    {
        internal Task? Running { get; set; }

        /// <summary>True once the parser exists: it, not the attempt, gives the Worker permit back.</summary>
        internal bool OwnsPermit { get; set; }
    }
}
