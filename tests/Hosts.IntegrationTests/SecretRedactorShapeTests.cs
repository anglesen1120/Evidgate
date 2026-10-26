using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.4 / AC2 shapes: one case per form a secret reaches a log line in — free text, a rendered
/// key/value pair, a JSON body, an <c>EVIDGATE_*</c> name — plus the bounds the redactor works under.
/// </summary>
/// <remarks>
/// The cases marked <c>QA D1</c>-<c>D3</c> are the shapes QA found unmasked on 2026-10-26; the
/// <c>F7</c>-<c>F9</c> cases pin the bounds and the marker list the performance/security review asked
/// for. Every case pairs an absence with a presence, so "nothing found" is never asserted against a
/// redactor that did nothing.
/// </remarks>
public sealed class SecretRedactorShapeTests
{
    private const string Canary = "Sup3r-Secret-P4ss";

    private const string VisibleValue = "visible-not-a-secret";

    private static readonly SecretRedactor Redactor = new();

    [Fact]
    public void Plain_Password_Assignment_Is_Masked()
    {
        var redacted = Redactor.RedactText("login password=" + Canary);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.Contains("password=" + SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Quoted_Key_Pair_Is_Masked_And_Its_Sibling_Survives()
    {
        // QA D1: the attribute LogRedactionPipelineTests rendered is `"Password"="…"`, so a quote sits
        // between the key and the separator and a key-directly-before-= pattern never matches.
        var redacted = Redactor.RedactText("{\"Password\"=\"" + Canary + "\",\"Visible\"=\"ok\"}");

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.Contains("Visible", redacted, StringComparison.Ordinal);
        Assert.Contains("ok", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Json_Colon_Pair_Is_Masked()
    {
        // QA D2: `:` is the separator of every JSON body, so the pattern needs it beside `=`.
        var redacted = Redactor.RedactText("{\"password\":\"" + Canary + "\"}");

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EVIDGATE__X__SECRET_VALUE=" + Canary)]
    [InlineData("EVIDGATE__X__SECRET_VALUE: " + Canary)]
    [InlineData("EVIDGATE__X__SIGNING_KEY=" + Canary)]
    public void An_Environment_Name_Whose_Secret_Word_Is_Not_Last_Is_Masked(string text)
    {
        // QA D3: the name is `EVIDGATE_*_SECRET_VALUE`, so the secret word is followed by another
        // segment, not by the separator.
        var redacted = Redactor.RedactText(text);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Hyphenated_Api_Key_Assignment_Is_Masked()
    {
        var redacted = Redactor.RedactText("api-key=" + Canary);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Authorization_Header_Value_Is_Masked()
    {
        var redacted = Redactor.RedactText("Authorization: Bearer " + Canary);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization: " + SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SigningKey")]
    [InlineData("encryption_key")]
    [InlineData("EVIDGATE__X__SIGNING_KEY")]
    [InlineData("Signature")]
    [InlineData("Sas")]
    [InlineData("SessionId")]
    public void A_Key_That_Names_A_Signing_Encryption_Signature_Sas_Or_Session_Key_Is_Erased(string key)
    {
        // F9: a structured pair is exported by key, so the marker list is the only thing that can
        // catch these names — text-level `key=` never sees a structured value.
        Assert.Equal(SecretRedactor.Mask, Redactor.RedactValue(key, Canary));
    }

    [Fact]
    public void A_Visible_Key_And_A_Session_Shaped_Value_Are_Not_Touched()
    {
        // The markers match the key, never the value: a status token that spells a session code
        // must survive, or every log line loses its outcome.
        Assert.Equal(VisibleValue, Redactor.RedactValue("Visible", VisibleValue));
        Assert.Equal("session_expired", Redactor.RedactValue("Outcome", "session_expired"));
    }

    [Theory]
    [InlineData("{\"Password\"=\"" + Canary + "\"}")]
    [InlineData("{\"password\":\"" + Canary + "\"}")]
    public void A_Value_Whose_Text_Carries_A_Keyed_Pair_Is_Masked(string value)
    {
        // QA D1 at the value seam: a dictionary entry (or a state pair) whose ToString is a keyed
        // pair goes through the same text rules.
        var redacted = Redactor.RedactValue("Payload", value);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_Text_Is_Bounded_Before_The_Patterns_Run()
    {
        // F7: the patterns never see more than a bounded prefix, so a long message cannot become an
        // unbounded scan on the logging thread.
        var secretBeyondTheBound = new string('a', 20_000) + " Password=" + Canary;
        var secretInsideTheBound = "Password=" + Canary + new string('b', 20_000);

        var beyond = Redactor.RedactText(secretBeyondTheBound);
        var inside = Redactor.RedactText(secretInsideTheBound);

        Assert.DoesNotContain(Canary, beyond, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, inside, StringComparison.Ordinal);
        Assert.True(
            beyond.Length < 10_000,
            $"the redacted text must be bounded, but it was {beyond.Length} characters");
        Assert.True(
            inside.Length < 1_000,
            $"the masked pair must collapse to the mask, but the line was {inside.Length} characters");
    }

    [Fact]
    public void Unterminated_Quotes_Finish_And_Leak_Nothing()
    {
        // F7's quadratic shape: a run of `key="` with no closing quote. The quoted patterns must not
        // read the run as one value (their closing quote has to be followed by a delimiter), so the
        // unquoted pattern still masks it and the call returns inside its bound.
        var pathological = string.Concat(Enumerable.Repeat("key=\"", 1_000)) + Canary;
        var stopwatch = Stopwatch.StartNew();

        var redacted = Redactor.RedactText(pathological);

        stopwatch.Stop();
        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("key=\"", redacted, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"one redaction pass took {stopwatch.Elapsed}, which is a stall on the logging thread");
    }

    [Fact]
    public void A_Quoted_Secret_That_Spans_Lines_Is_Masked_To_Its_End()
    {
        // A quoted value rendered into a multi-line exception message: a value class bounded at the
        // line break masks only the head and emits the tail, which is a leak the line-bounded
        // unquoted pattern cannot recover.
        const string Tail = "tail-of-the-secret";
        var doubleQuoted = Redactor.RedactText("login failed: Password=\"" + Canary + "\n  " + Tail + "\"");
        var singleQuoted = Redactor.RedactText("login failed: Password='" + Canary + "\n  " + Tail + "'");

        foreach (var redacted in new[] { doubleQuoted, singleQuoted })
        {
            Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(Tail, redacted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Password=***" + Canary)]
    [InlineData("{\"token\":\"***" + Canary + "\"}")]
    [InlineData("__Host-session=***" + Canary + "; theme=dark")]
    public void A_Value_That_Begins_With_The_Mask_Is_Still_Masked(string text)
    {
        // The idempotence guard skips an exact mask value only: a value that merely starts with the
        // mask is probed like any other, or `***` becomes a way to opt a secret out of masking.
        var redacted = Redactor.RedactText(text);

        Assert.DoesNotContain(Canary, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("worker started with 8 consumers at 12:00:00")]
    [InlineData("the token was refreshed")]
    [InlineData("elapsed: 12ms")]
    [InlineData("the secret was rotated at login")]
    [InlineData("System.InvalidOperationException: nothing here is a secret")]
    public void A_Clean_Line_Is_Returned_As_The_Instance_It_Came_In(string text)
    {
        // F8: no separator or no marker means no pattern can match, so the hot path allocates nothing
        // and the callers' "nothing changed" checks (identity, `Equals`) stay cheap.
        Assert.Same(text, Redactor.RedactText(text));
    }

    [Fact]
    public void A_Marker_Word_Without_An_Assignment_Is_Not_A_Secret()
    {
        // The pre-check requires the marker on the identifier that carries the separator. A type name
        // followed by a colon and a prose sentence that happens to contain a secret word - the shape of
        // every exception message this suite passes through - must reach no pattern at all, or a pass
        // that fails closed replaces the whole line with the mask and the message is lost.
        var original = new InvalidOperationException("nothing here is a secret");
        var text = original.ToString();

        Assert.Same(original, Redactor.RedactException(original));
        Assert.Same(original.Message, Redactor.RedactText(original.Message));
        Assert.Same(text, Redactor.RedactText(text));
    }

    [Theory]
    [InlineData("Password=" + Canary)]
    [InlineData("Password=\"" + Canary + "\"")]
    [InlineData("{\"password\":\"" + Canary + "\"}")]
    [InlineData("EVIDGATE__X__SECRET_VALUE=" + Canary)]
    [InlineData("Cookie: __Host-session=" + Canary + "; theme=dark")]
    [InlineData("Authorization: Bearer " + Canary)]
    [InlineData("Host=db.internal;Port=5432;Password=" + Canary + ";Database=evidgate")]
    public void Redaction_Is_Idempotent(string text)
    {
        // F8: the decorator and the export processor both run it, so a second pass over an already
        // masked line must not re-wrap the mask or eat the rest of the line.
        var once = Redactor.RedactText(text);

        Assert.Equal(once, Redactor.RedactText(once));
    }
}
