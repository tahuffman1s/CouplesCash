using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CouplesCash.Api.Domain;

/// <summary>
/// Dedupe key for imported transactions (<c>UNIQUE (household_id, fingerprint)</c>).
/// With a provider id: <c>sha256(source || '|' || scope || '|' || external_id)</c> — SimpleFIN ids are only unique
/// within an account, so the external account id is the scope. Without one (CSV):
/// <c>sha256(account_id || '|' || posted_on || '|' || amount_cents || '|' || normalise(description))</c>.
/// </summary>
public static class Fingerprint
{
    public static byte[] ForExternal(string source, string scope, string externalId) =>
        Hash($"{source}|{scope}|{externalId}");

    public static byte[] ForRow(Guid accountId, DateOnly postedOn, long amountCents, string description) =>
        Hash($"{accountId:D}|{postedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{amountCents.ToString(CultureInfo.InvariantCulture)}|{Normalise(description)}");

    /// <summary>Lower-case, collapsed whitespace, no punctuation — stable across "KROGER #123" vs "Kroger  #123".</summary>
    public static string Normalise(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "";
        }

        var sb = new StringBuilder(description.Length);
        var pendingSpace = false;
        foreach (var ch in description.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
