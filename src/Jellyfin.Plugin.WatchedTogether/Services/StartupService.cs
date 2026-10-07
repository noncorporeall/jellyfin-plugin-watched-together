using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>
/// Runs once at server startup (after every plugin has loaded) and hooks into
/// Home Screen Sections and File Transformation. Retries for a while in case
/// those plugins are still initialising.
/// </summary>
public class StartupService : IScheduledTask
{
    private const int MaxAttempts = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<StartupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Watched Together Startup";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.WatchedTogether.Startup";

    /// <inheritdoc />
    public string Description => "Registers the Watched Together shelf with Home Screen Sections and injects the avatar badges into the web client.";

    /// <inheritdoc />
    public string Category => "Startup Services";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        bool sectionDone = false;
        bool injectionDone = false;

        for (int attempt = 1; attempt <= MaxAttempts && !(sectionDone && injectionDone); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!sectionDone)
            {
                sectionDone = TryRun(() => IntegrationRegistrar.RegisterSection(_logger), "section registration", attempt);
            }

            if (!injectionDone)
            {
                injectionDone = TryRun(() => IntegrationRegistrar.RegisterClientInjection(_logger), "client injection", attempt);
            }

            progress.Report(100.0 * attempt / MaxAttempts);

            if (!(sectionDone && injectionDone) && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!sectionDone)
        {
            _logger.LogError("[WatchedTogether] Gave up registering the shelf. Is 'Home Screen Sections' installed and active?");
        }

        if (!injectionDone)
        {
            _logger.LogError("[WatchedTogether] Gave up injecting avatar badges. Is 'File Transformation' installed and active?");
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };
    }

    private bool TryRun(Func<bool> action, string what, int attempt)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WatchedTogether] {What} attempt {Attempt} failed", what, attempt);
            return false;
        }
    }
}
