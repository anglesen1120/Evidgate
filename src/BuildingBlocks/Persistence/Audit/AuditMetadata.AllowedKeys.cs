namespace Evidgate.BuildingBlocks.Persistence.Audit;

// The key list lives beside the main file so that file stays under the size limit.
public static partial class AuditMetadata
{
    /// <summary>The keys handlers may record. Adding one is a review decision.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "email_domain",
        "role_code",
        "role_id",
        "membership_id",
        "slug",
        "status",
        "fields",
        "count",
        "format",
        "range_days",
        "reason",
        "outcome",
        "run_id",
        // 07.1.1-T4.1: the activation records which revision it moved the pointer from and the mode it stored.
        "previous_id",
        "mode",
        // 02.3.1-T2: login/link/bind rows record which provider authenticated the identity and how an invite bound (names, never an address or subject).
        "provider",
        "via",
        // 02.3.1-T3: the own-profile write records which language the account chose (`vi` / `en`), never free text.
        "locale",
        // F55 (02.3.2-T2.1 AC6): the administrative session revoke records whose session it ended — the member's user id, never the session's credential.
        "target_user_id",
        // The all-sessions admin revoke records how many live sessions one call ended, as a decimal string.
        "revoked_count",
        // The append-only version chain records which version the audited save produced; a number, never content.
        "version_no",
        // 06.2.1: the kind-policy override records the sensitivity the row carried before the write.
        "previous_default_sensitivity",
        // 06.2.2: the kind-policy override records how many redaction rules the write left on the kind — a count, never
        // a pattern — and, when it replaced the declared set, how many it replaced.
        "redaction_rule_count",
        "redaction_rule_count_before",
        // The audited billing reads record which slice of an org's accounting records was read: the period and metric
        // filters (enumerated values), the page bound and whether a continuation was used. None of them is an
        // amount, a price or a free-text name — and never the opaque continuation itself, which is why the row
        // records that one was present instead of its value — so an audit row names the query without recording
        // what it billed.
        "period",
        "metric",
        "limit",
        "has_cursor",
        // A late ledger row the insert guard re-dated records the month it was metered for next to the month it was
        // billed in; both are `YYYY-MM` shapes, never an amount or a key.
        "original_period",
        // A call the redaction gate refused names the catalogue rule that still matched, the 1-based segment it sat in
        // (0 when it sat outside a segment) and the digest of the redaction report that describes it. All three are
        // identifiers or numbers; the text that matched never reaches the row.
        "rule_id",
        "segment_ordinal",
        "report_hash",
    };
}
