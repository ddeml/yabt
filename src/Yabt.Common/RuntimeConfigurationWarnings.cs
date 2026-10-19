using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Yabt.Common;

public static class RuntimeConfigurationWarnings
{
    /// <summary>
    /// Inspects a provider's writable scalar and nested options properties before standard binding.
    /// Runtime setting paths, including unsupported-setting declarations, are case-insensitive.
    /// </summary>
    public static void Inspect<TOptions>
    (
        IConfiguration configuration,
        string sectionPath,
        ILogger logger,
        IReadOnlyDictionary<string, string>? unsupportedSettings = null
    )
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        InspectSection
        (
            configuration,
            configuration.GetSection(sectionPath),
            typeof(TOptions),
            logger,
            sectionPath,
            unsupportedSettings?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
        );
    }

    private static void InspectSection
    (
        IConfiguration configuration,
        IConfigurationSection section,
        Type optionsType,
        ILogger logger,
        string rootSectionPath,
        IReadOnlyDictionary<string, string>? unsupportedSettings
    )
    {
        if (section.Value is not null)
        {
            throw new YabtException
            (
                $"Configuration setting '{section.Path}' must be an object."
            );
        }

        var properties = optionsType.GetProperties(BindingFlags.Instance | BindingFlags.Public).
            Where(property => property.SetMethod?.IsPublic == true).
            ToDictionary
            (
                property => property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ??
                    property.Name,
                StringComparer.OrdinalIgnoreCase
            );

        foreach (var child in section.GetChildren())
        {
            if (!properties.TryGetValue(child.Key, out var property))
            {
                ConfigurationWarnings.Report
                (
                    logger,
                    GetSource(configuration, child),
                    child.Path,
                    "The setting is unknown and will be ignored."
                );
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ??
                property.PropertyType;
            if (propertyType.IsValueType || propertyType == typeof(string) ||
                propertyType == typeof(Uri))
            {
                if (child.GetChildren().Any())
                {
                    throw new YabtException
                    (
                        $"Configuration setting '{child.Path}' must be a scalar value."
                    );
                }

                if (propertyType.IsEnum && child.Value is { } enumText &&
                    (!Enum.TryParse(propertyType, enumText, ignoreCase: true, out var enumValue) ||
                    !Enum.IsDefined(propertyType, enumValue)))
                {
                    throw new YabtException
                    (
                        $"Configuration setting '{child.Path}' is not a supported {propertyType.Name} value."
                    );
                }
            }
            else
            {
                InspectSection
                (
                    configuration,
                    child,
                    propertyType,
                    logger,
                    rootSectionPath,
                    unsupportedSettings
                );
            }

            var relativePath = child.Path[(rootSectionPath.Length + 1)..];
            if (unsupportedSettings is not null &&
                unsupportedSettings.TryGetValue(relativePath, out var behavior) &&
                (!string.IsNullOrEmpty(child.Value) || child.GetChildren().Any()))
            {
                ConfigurationWarnings.Report
                (
                    logger,
                    GetSource(configuration, child),
                    child.Path,
                    behavior
                );
            }
        }
    }

    private static string GetSource
    (
        IConfiguration configuration,
        IConfigurationSection section
    )
    {
        if (configuration is IConfigurationRoot root)
        {
            var evidencePath = GetFirstValuePath(section);
            foreach (var provider in root.Providers.Reverse())
            {
                if (!provider.TryGet(evidencePath, out _)) continue;

                // The framework JSON provider's diagnostic description names its source file.
                // Do not print arbitrary provider descriptions, which may include secret values.
                return provider.GetType().FullName ==
                    "Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider" ?
                    provider.ToString() ?? "runtime configuration" :
                    "runtime configuration";
            }
        }

        return "runtime configuration";
    }

    private static string GetFirstValuePath(IConfigurationSection section)
    {
        if (section.Value is not null) return section.Path;

        var firstChild = section.GetChildren().FirstOrDefault();
        return firstChild is null ? section.Path : GetFirstValuePath(firstChild);
    }
}
