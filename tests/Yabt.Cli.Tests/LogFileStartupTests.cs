using Microsoft.Extensions.Logging;
using Yabt.Cli.Implementation;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class LogFileStartupTests
{
    [TestMethod]
    public void PrepareDoesNotCreateFileUntilStart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"yabt-cli-startup-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "run.log");
        try
        {
            using (var startup = LogFileStartup.Prepare(new(true, path)))
            {
                Assert.IsFalse(File.Exists(path));

                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Debug);
                    startup.Attach(builder);
                });
                var logger = loggerFactory.CreateLogger("Yabt.Tests.Startup");
                logger.LogDebug("buffered before path approval");

                Assert.IsFalse(File.Exists(path));

                startup.Start();
                logger.LogDebug("written after path approval");

                Assert.IsTrue(File.Exists(path));
            }

            var content = File.ReadAllText(path);
            StringAssert.Contains(content, "buffered before path approval");
            StringAssert.Contains(content, "written after path approval");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
