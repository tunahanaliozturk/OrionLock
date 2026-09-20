namespace Moongazing.OrionLock.Consul;

using System.Text;

/// <summary>
/// Makes a lock key safe to splice into Consul's <c>/v1/kv/{key}</c> HTTP path.
/// </summary>
/// <remarks>
/// The key reaches Consul.NET as a <c>KVPair</c> key, which is concatenated after <c>/v1/kv/</c> into a
/// <c>UriBuilder.Path</c>. Two things happen there that a caller would not expect. .NET canonicalises the
/// URI, so a <c>/../</c> inside the key COLLAPSES and the request lands on a different Consul endpoint
/// entirely - <c>../session/destroy/{id}</c> retargets a KV acquire at session destruction. And <c>?</c>
/// or <c>#</c> start a query or fragment, splicing or truncating the request. Percent-encoding each
/// segment neutralises the delimiters; the traversal segments cannot be encoded away, because .NET
/// unescapes <c>%2E</c> back to <c>.</c> during canonicalisation, so they are rejected instead.
/// <para>
/// This is per-backend hardening of a rule that belongs to the whole library: the key is caller data on
/// every backend, and only a shared validator in the core can guarantee one rule for all of them.
/// </para>
/// </remarks>
internal static class ConsulKvPath
{
    /// <summary>
    /// Returns the key with every <c>/</c>-separated segment percent-encoded, preserving the hierarchy a
    /// <see cref="ConsulLockOptions.KeyPrefix"/> deliberately expresses.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A segment is empty or is a relative-path segment (<c>.</c> or <c>..</c>).
    /// </exception>
    internal static string Encode(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var segments = key.Split('/');
        var builder = new StringBuilder(key.Length + 8);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                throw new ArgumentException(
                    $"Lock key '{key}' has an empty path segment. A leading, trailing or doubled '/' "
                    + "collapses during URI canonicalisation and would address a different Consul key.",
                    parameterName);
            }

            if (segment is "." or "..")
            {
                throw new ArgumentException(
                    $"Lock key '{key}' contains the relative path segment '{segment}'. URI "
                    + "canonicalisation would resolve it and send the request to a different Consul "
                    + "endpoint than the KV store.",
                    parameterName);
            }

            if (i > 0)
            {
                builder.Append('/');
            }

            builder.Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }
}
