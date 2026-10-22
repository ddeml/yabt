using System.Buffers;
using System.Text;

namespace Yabt.Sync.Implementation;

internal static class SyncFailureReporting
{
    internal static List<SyncItemFailure> NormalizeFailures
    (
        IEnumerable<SyncItemFailure> failures
    ) => failures
        .Distinct()
        .OrderBy(failure => failure.RelativePath, StringComparer.Ordinal)
        .ThenBy(failure => failure.Operation, StringComparer.Ordinal)
        .ThenBy(failure => failure.Reason, StringComparer.Ordinal)
        .ToList();

    internal static string BuildIncompleteReadMessage
    (
        string operationName,
        List<SyncItemFailure> failures,
        string safetyMessage
    )
    {
        var message = new StringBuilder();
        message.Append(operationName);
        message.Append(" incomplete: ");
        message.Append(failures.Count);
        message.Append(" item(s) could not be read safely. ");
        message.Append(safetyMessage);
        message.AppendLine();
        message.AppendLine("Unreadable items:");
        foreach (var failure in failures)
        {
            message.Append("- ");
            message.Append(failure.RelativePath);
            message.Append(" [");
            message.Append(failure.Operation);
            message.Append("]: ");
            message.AppendLine(failure.Reason);
        }

        return message.ToString().TrimEnd();
    }

    internal static string GetFailureReason(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            var aggregateReasons = aggregateException
                .Flatten()
                .InnerExceptions
                .Select(GetFailureReason)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return string.Join(" | ", aggregateReasons);
        }

        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return messages.Count == 0 ?
            exception.GetType().Name :
            string.Join(" Caused by: ", messages);
    }
}
