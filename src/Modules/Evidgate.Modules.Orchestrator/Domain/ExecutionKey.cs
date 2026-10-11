using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// The server-side <c>execution_key</c> layout of 05.2.1 (00 §7, ruling T10; T3.1 owns the golden vectors and the
/// reporter wiring). This class is the <em>only</em> place the byte layout lives, so an owner override is a one-file
/// change.
/// <para>
/// The key is the lowercase-hex SHA-256 over three length-prefixed UTF-8 fields, in this order: <c>case_id</c> (the
/// Guid's <c>"D"</c> form, lowercase), <c>variant_hash</c> (the full <c>sha256:&lt;hex&gt;</c> string Catalog's
/// <c>VariantNormalizer</c> gives the suite member) and <c>target_env</c>. Each field is a 4-byte big-endian byte
/// length followed by its UTF-8 bytes, so no separator ambiguity can make two different tuples read the same
/// (<c>("ab","c")</c> and <c>("a","bc")</c> hash differently). The result is 64 lowercase hex characters, the shape
/// <c>planned_execution.execution_key</c>'s CHECK and <c>artifact.execution_key</c>'s CHECK (migration 0028) require.
/// </para>
/// </summary>
internal static class ExecutionKey
{
    /// <summary>The <c>execution_key</c> of one planned execution.</summary>
    /// <param name="caseId">The case the execution runs (never the revision id).</param>
    /// <param name="variantHash">The normalised variant hash, <c>sha256:&lt;hex&gt;</c>.</param>
    /// <param name="targetEnv">The target environment label.</param>
    internal static string Compute(Guid caseId, string variantHash, string targetEnv)
    {
        ArgumentNullException.ThrowIfNull(variantHash);
        ArgumentNullException.ThrowIfNull(targetEnv);

        var caseBytes = Encoding.UTF8.GetBytes(caseId.ToString("D"));
        var variantBytes = Encoding.UTF8.GetBytes(variantHash);
        var envBytes = Encoding.UTF8.GetBytes(targetEnv);

        var buffer = new byte[4 + caseBytes.Length + 4 + variantBytes.Length + 4 + envBytes.Length];
        var offset = 0;
        Write(buffer, ref offset, caseBytes);
        Write(buffer, ref offset, variantBytes);
        Write(buffer, ref offset, envBytes);

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    private static void Write(byte[] destination, ref int offset, byte[] field)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, sizeof(int)), field.Length);
        offset += sizeof(int);
        field.CopyTo(destination, offset);
        offset += field.Length;
    }
}
