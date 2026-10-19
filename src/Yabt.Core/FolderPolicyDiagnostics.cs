using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Core;

public static class FolderPolicyDiagnostics
{
    private static readonly ConditionalWeakTable<FolderPolicy, string> ConfigurationSources = new();

    public static void SetConfigurationSource(FolderPolicy policy, string configurationSource)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrEmpty(configurationSource);

        ConfigurationSources.AddOrUpdate(policy, configurationSource);
    }

    public static string? GetConfigurationSource(FolderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return ConfigurationSources.TryGetValue(policy, out var source) ? source : null;
    }

    public static void Validate(FolderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(policy.Format))
        {
            throw new YabtCoreException("Folder policy setting '$.format' must be a nonempty string.");
        }

        ValidatePatterns(policy.IncludePatterns, "$.includePatterns");
        ValidatePatterns(policy.ExcludePatterns, "$.excludePatterns");
        _ = HasOptions(policy.Options);
    }

    public static void ReportIgnoredPatterns
    (
        FolderPolicy policy,
        ILogger logger,
        string configurationSource = ".yabt-policy.json"
    )
    {
        Validate(policy);

        var source = GetConfigurationSource(policy) ?? configurationSource;
        if (policy.IncludePatterns?.Any() == true)
        {
            ConfigurationWarnings.Report
            (
                logger,
                source,
                "$.includePatterns",
                "This setting is not supported and will be ignored. " +
                    "Files outside these patterns may also be archived."
            );
        }

        if (policy.ExcludePatterns?.Any() == true)
        {
            ConfigurationWarnings.Report
            (
                logger,
                source,
                "$.excludePatterns",
                "This setting is not supported and will be ignored. " +
                    "Files matching these patterns may be archived."
            );
        }
    }

    public static void ReportIgnoredOptions
    (
        FolderPolicy policy,
        ILogger logger,
        string behavior,
        string configurationSource = ".yabt-policy.json"
    )
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (HasOptions(policy.Options))
        {
            ConfigurationWarnings.Report
            (
                logger,
                GetConfigurationSource(policy) ?? configurationSource,
                "$.options",
                behavior
            );
        }
    }

    private static void ValidatePatterns(IEnumerable<string>? patterns, string propertyPath)
    {
        if (patterns is null) { return; }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new YabtCoreException
                (
                    $"Folder policy setting '{propertyPath}' must contain nonempty strings."
                );
            }
        }
    }

    private static bool HasOptions(object? options)
    {
        if (options is null) { return false; }

        var json = options is JsonElement element ? element : JsonSerializer.SerializeToElement(options);
        if (json.ValueKind == JsonValueKind.Null) { return false; }
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new YabtCoreException("Folder policy setting '$.options' must be an object or null.");
        }

        return json.EnumerateObject().Any();
    }
}
