using System.CommandLine;

namespace Yabt.Cli.Implementation;

internal readonly record struct LogFileRequest
(
    bool IsEnabled,
    string? ExplicitPath
);

internal sealed record LogFileCommandLineResult
(
    LogFileRequest Request,
    string[] RemainingArguments
);

internal static class LogFileCommandLine
{
    private const string OptionName = "--log-file";
    private const string OptionValuePrefix = OptionName + "=";

    public static Option<bool> CreateOption() => new(OptionName)
    {
        Description = "Also write logs to a new file. Use --log-file=PATH to choose its path.",
        Recursive = true,
    };

    public static LogFileCommandLineResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var remainingArguments = new List<string>(args.Count);
        var request = new LogFileRequest(false, null);
        var foundOption = false;
        var reachedOptionTerminator = false;

        foreach (var argument in args)
        {
            if (reachedOptionTerminator)
            {
                remainingArguments.Add(argument);
                continue;
            }

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                reachedOptionTerminator = true;
                remainingArguments.Add(argument);
                continue;
            }

            string? explicitPath;
            if (string.Equals(argument, OptionName, StringComparison.Ordinal))
            {
                explicitPath = null;
            }
            else if (argument.StartsWith(OptionValuePrefix, StringComparison.Ordinal))
            {
                explicitPath = argument[OptionValuePrefix.Length..];
                if (string.IsNullOrWhiteSpace(explicitPath))
                {
                    throw new YabtCliException
                    (
                        $"{OptionName} requires a nonempty path after '='."
                    );
                }
            }
            else
            {
                remainingArguments.Add(argument);
                continue;
            }

            if (foundOption)
            {
                throw new YabtCliException($"{OptionName} may be specified only once.");
            }

            foundOption = true;
            request = new(true, explicitPath);
        }

        return new(request, [.. remainingArguments]);
    }
}
