namespace Evidgate.Modules.Healing.Infrastructure.Persistence;

/// <summary>
/// The statements of the AI credit writer. Each names the organization, so a foreign id matches no row on top of the
/// row level security policy.
/// </summary>
internal static class AiCreditSql
{
    /// <summary>The account of a first grant; a second grant finds it and inserts nothing.</summary>
    internal const string EnsureAccount = """
        insert into public.ai_credit_account (id, org_id, balance_micro, reserved_micro)
        values (@id, @org_id, 0, 0)
        on conflict (org_id) do nothing
        """;

    /// <summary>
    /// The account row under its row lock. Every writer of one account serializes here, so the balance the math reads
    /// is the balance the update changes and a replay check after this statement sees any committed first write.
    /// </summary>
    internal const string LockAccount = """
        select id, balance_micro as BalanceMicro, reserved_micro as ReservedMicro
          from public.ai_credit_account
         where org_id = @org_id
           for update
        """;

    /// <summary>The stored write of a billing key, for the replay check.</summary>
    internal const string FindEntry = """
        select id, payload_hash as PayloadHash
          from public.ai_credit_entry
         where org_id = @org_id and billing_key = @billing_key
        """;

    /// <summary>The project of the call an entry names; no row means the call is not this tenant's.</summary>
    internal const string CallProject = """
        select project_id
          from public.healing_llm_call
         where org_id = @org_id and id = @id
        """;

    /// <summary>The reserve and the settlements already written for one call.</summary>
    internal const string CallEntries = """
        select kind, amount_micro as AmountMicro
          from public.ai_credit_entry
         where org_id = @org_id and call_id = @call_id and kind in ('reserve', 'consume', 'refund')
        """;

    /// <summary>Stores the balance the math produced, on the row this transaction already holds locked.</summary>
    internal const string UpdateAccount = """
        update public.ai_credit_account
           set balance_micro = @balance_micro, reserved_micro = @reserved_micro
         where org_id = @org_id and id = @id
        """;

    /// <summary>The append-only entry.</summary>
    internal const string InsertEntry = """
        insert into public.ai_credit_entry
          (id, org_id, account_id, kind, amount_micro, call_id, billing_key, payload_hash, reason, actor)
        values
          (@id, @org_id, @account_id, @kind, @amount_micro, @call_id, @billing_key, @payload_hash, @reason, @actor)
        """;
}
