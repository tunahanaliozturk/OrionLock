namespace Moongazing.OrionLock.Consul;

using System.Text;

/// <summary>
/// Percent-encodes a Consul KV path so it survives being spliced into <c>/v1/kv/{path}</c>.
/// </summary>
/// <remarks>
/// The path reaches Consul.NET as a <c>KVPair</c> key, which is concatenated after <c>/v1/kv/</c> into a
/// <c>UriBuilder.Path</c>, where <c>?</c> or <c>#</c> would start a query or fragment and splice or
/// truncate the request. Encoding each segment neutralises the delimiters.
/// <para>
/// This is now only an encoder. The rules that decide whether a key is acceptable at all - no <c>/</c>,
/// no <c>.</c> or <c>..</c>, no control characters, a length bound - live in
/// <see cref="Moongazing.OrionLock.LockKey"/> in the core, so all seven backends refuse the same keys on
/// the caller's own thread. What reaches here is the option-supplied <see cref="ConsulLockOptions.KeyPrefix"/>
/// followed by an already-validated key; splitting on <c>/</c> preserves the hierarchy the prefix
/// deliberately expresses.
/// </para>
/// </remarks>
internal static class ConsulKvPath
{
    /// <summary>Returns <paramref name="path"/> with every <c>/</c>-separated segment percent-encoded.</summary>
    internal static string Encode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('/');
        var builder = new StringBuilder(path.Length + 8);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('/');
            }

            builder.Append(Uri.EscapeDataString(segments[i]));
        }

        return builder.ToString();
    }
}
