using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Same as extended class System.Exception
namespace System;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Extension methods for the <see cref="Exception"/> class.
/// </summary>
public static class YabtExceptionExtensions
{
    /// <summary>
    /// <see href="https://github.com/search?q=repo%3Adotnet%2FSqlClient+%22SQL_OperationCancelled%22+language%3AXML&amp;type=code&amp;l=XML"/>
    /// </summary>
    static readonly FrozenSet<string> _sqlOperationCancelledMessages =
    [
        "Operace byla zrušena uživatelem.", //cs
        "Der Vorgang wurde vom Benutzer abgebrochen.", //de
        "Operation cancelled by user.", //en
        "Operación cancelada por el usuario.", //es
        "Opération annulée par l'utilisateur.", //fr
        "Operazione annullata dall'utente.", //it
        "操作はユーザーによって取り消されました。", //ja
        "사용자가 작업을 취소했습니다.", //ko
        "Operacja została anulowana przez użytkownika.", //pl
        "İşlem kullanıcı tarafından iptal edildi.", //tr
        "Operação cancelada pelo usuário.", //pt-BR
        "用户已取消操作。", //zh-Hans
        "使用者已經取消作業。", //zh-Hant
    ];

    public static bool ContainsException<TException>
    (
        this Exception exception,
        Func<TException, bool>? predicate,
        [MaybeNullWhen(false)] out TException foundException
    ) where TException : Exception
    {
        var stack = new Stack<Exception>();
        while (true)
        {
            if (exception is TException typedException && (predicate is null || predicate(typedException)))
            {
                foundException = typedException;
                return true;
            }
            if (exception is AggregateException aggregateException)
            {
                foreach (Exception innerException in aggregateException.InnerExceptions)
                {
                    stack.Push(innerException);
                }
            }
            else
            {
                var innerException = exception.InnerException;
                if (innerException is not null)
                {
                    stack.Push(innerException);
                }
            }
            if (stack.Count == 0)
            {
                foundException = null;
                return false;
            }
            exception = stack.Pop();
        }
    }

    public static bool ContainsException<TException>
    (
        this Exception exception,
        Func<TException, bool>? predicate
    ) where TException : Exception =>
        exception.ContainsException(predicate, out _);

    public static bool ContainsException<TException>
    (
        this Exception exception,
        [MaybeNullWhen(false)] out TException foundException
    ) where TException : Exception =>
        exception.ContainsException(null, out foundException);

    public static bool ContainsException<TException>
    (
        this Exception exception
    ) where TException : Exception =>
        exception.ContainsException<TException>(null, out _);

    public static bool IsCancellationException(this Exception exception, Func<Exception, bool>? predicate = default) =>
        exception.ContainsException<Exception>(exception =>
        {
            bool Predicate() => predicate?.Invoke(exception) ?? true;
            if (exception is OperationCanceledException)
            {
                return Predicate();
            }
            var exceptionType = exception.GetType();
            switch (exceptionType.FullName)
            {
                case "Microsoft.Data.SqlClient.SqlException":
                    if (_sqlOperationCancelledMessages.Contains(exception.Message))
                    {
                        return Predicate();
                    }
                    break;
                case "Oracle.ManagedDataAccess.Client.OracleException":
                    if (1013.Equals(exceptionType.GetProperty("Number")?.GetValue(exception))) //ORA-01013: user requested cancel of current operation
                    {
                        return Predicate();
                    }
                    break;
                case "Microsoft.Identity.Client.MsalClientException":
                    if ("authentication_canceled".Equals(exceptionType.GetProperty("ErrorCode")?.GetValue(exception)))
                    {
                        return Predicate();
                    }
                    break;
            }
            return false;
        });
}
