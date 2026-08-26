using Yabt.Cli;
using Yabt.Cli.Implementation;
using Microsoft.Extensions.Hosting;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class LogFileCommandLineTests
{
    [TestMethod]
    public void ParseLeavesFileLoggingDisabledWhenOptionIsAbsent()
    {
        var result = LogFileCommandLine.Parse(["backup"]);

        Assert.IsFalse(result.Request.IsEnabled);
        Assert.IsNull(result.Request.ExplicitPath);
        CollectionAssert.AreEqual(new[] { "backup" }, result.RemainingArguments);
    }

    [TestMethod]
    public void ParseBareOptionEnablesDefaultFileAndLeavesFollowingSourceArgument()
    {
        var result = LogFileCommandLine.Parse(["backup", "--log-file", "source"]);

        Assert.IsTrue(result.Request.IsEnabled);
        Assert.IsNull(result.Request.ExplicitPath);
        CollectionAssert.AreEqual(new[] { "backup", "source" }, result.RemainingArguments);
    }

    [TestMethod]
    [DataRow("--log-file", "backup")]
    [DataRow("backup", "--log-file")]
    public void ParseAcceptsDefaultOptionBeforeOrAfterSubcommand(params string[] args)
    {
        var result = LogFileCommandLine.Parse(args);

        Assert.IsTrue(result.Request.IsEnabled);
        Assert.IsNull(result.Request.ExplicitPath);
        CollectionAssert.AreEqual(new[] { "backup" }, result.RemainingArguments);
    }

    [TestMethod]
    [DataRow("--log-file=logs/run.log", "backup")]
    [DataRow("backup", "--log-file=logs/run.log")]
    [DataRow("backup", "source", "--log-file=logs/run.log")]
    public void ParseAcceptsEqualsFormForExplicitPath(params string[] args)
    {
        var result = LogFileCommandLine.Parse(args);
        var expectedArguments = args
            .Where(static argument => !argument.StartsWith("--log-file=", StringComparison.Ordinal))
            .ToArray();

        Assert.IsTrue(result.Request.IsEnabled);
        Assert.AreEqual("logs/run.log", result.Request.ExplicitPath);
        CollectionAssert.AreEqual(expectedArguments, result.RemainingArguments);
    }

    [TestMethod]
    public void ParseRejectsRepeatedOption()
    {
        var exception = Assert.ThrowsExactly<YabtCliException>(() =>
            LogFileCommandLine.Parse
            ([
                "backup",
                "--log-file",
                "--log-file=second.log",
            ]));

        StringAssert.Contains(exception.Message, "--log-file");
    }

    [TestMethod]
    public void ParseRejectsEmptyEqualsPath()
    {
        var exception = Assert.ThrowsExactly<YabtCliException>(() =>
            LogFileCommandLine.Parse(["backup", "--log-file="]));

        StringAssert.Contains(exception.Message, "nonempty path");
    }

    [TestMethod]
    public void ParseDoesNotTreatArgumentsAfterOptionTerminatorAsLoggingOptions()
    {
        var args = new[] { "backup", "--", "--log-file" };

        var result = LogFileCommandLine.Parse(args);

        Assert.IsFalse(result.Request.IsEnabled);
        CollectionAssert.AreEqual(args, result.RemainingArguments);
    }

    [TestMethod]
    [DataRow("--log-file", "backup", "source")]
    [DataRow("backup", "source", "--log-file")]
    [DataRow("--log-file=logs/run.log", "backup", "source")]
    [DataRow("backup", "source", "--log-file=logs/run.log")]
    public void HostBuilderAcceptsArgumentsAfterLoggingOptionIsRemoved(params string[] args)
    {
        var originalArgs = args.ToArray();
        var result = LogFileCommandLine.Parse(args);

        var builder = Host.CreateApplicationBuilder(result.RemainingArguments);
        using var host = builder.Build();

        CollectionAssert.AreEqual(originalArgs, args);
        CollectionAssert.DoesNotContain(result.RemainingArguments, "--log-file");
        Assert.IsFalse
        (
            result.RemainingArguments.Any
            (
                static argument => argument.StartsWith("--log-file=", StringComparison.Ordinal)
            )
        );
    }
}
