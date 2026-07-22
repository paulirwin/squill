using Microsoft.Extensions.Logging;

namespace Squill.Aspire.Hosting;

internal class LoggerProgress(ILogger logger) : IProgress<string>
{
    public void Report(string value)
    {
        logger.LogInformation(value);
    }
}
