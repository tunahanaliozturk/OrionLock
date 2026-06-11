namespace Moongazing.OrionLock.DependencyInjection;

using System.Collections.Generic;
using Moongazing.OrionLock.Diagnostics;

/// <summary>v0.3.12 metrics-label extensions for <see cref="OrionLockBuilder"/>.</summary>
public static class OrionLockBuilderMetricsExtensions
{
    /// <summary>
    /// Stamp a static <c>(key, value)</c> tag on every counter / histogram OrionLock
    /// emits. Multi-tenant deployments use this to split dashboards by tenant / region
    /// without registering a separate <see cref="System.Diagnostics.Metrics.Meter"/>.
    /// Repeated calls accumulate; the LAST value for a given key wins.
    /// </summary>
    /// <param name="builder">The OrionLock builder.</param>
    /// <param name="key">Tag key.</param>
    /// <param name="value">Tag value.</param>
    public static OrionLockBuilder WithMetricsLabel(this OrionLockBuilder builder, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        // Read the current snapshot, merge in the new tag, write back atomically.
        var current = OrionLockDiagnostics.StaticTags;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in current)
        {
            if (pair.Value is string existing)
            {
                dict[pair.Key] = existing;
            }
        }
        dict[key] = value;
        OrionLockDiagnostics.SetStaticTags(dict);
        return builder;
    }

    /// <summary>
    /// Bulk overload: stamps every entry in <paramref name="tags"/> as a static metrics
    /// label. Conflicting keys: later entries win. The dictionary is COPIED into the
    /// snapshot so subsequent mutations on the caller's reference do not affect emitted
    /// measurements.
    /// </summary>
    public static OrionLockBuilder WithMetricsLabels(this OrionLockBuilder builder, IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tags);

        var current = OrionLockDiagnostics.StaticTags;
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in current)
        {
            if (pair.Value is string existing)
            {
                merged[pair.Key] = existing;
            }
        }
        foreach (var (k, v) in tags)
        {
            merged[k] = v;
        }
        OrionLockDiagnostics.SetStaticTags(merged);
        return builder;
    }
}
