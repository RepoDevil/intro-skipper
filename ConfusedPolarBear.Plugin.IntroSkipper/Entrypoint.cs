using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Server entrypoint.
/// </summary>
public class Entrypoint : IServerEntryPoint
{
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Entrypoint> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private Timer _queueTimer;
    private bool _analyzeAgain;

    /// <summary>
    /// Initializes a new instance of the <see cref="Entrypoint"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="userViewManager">User view manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public Entrypoint(
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<Entrypoint> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userViewManager = userViewManager;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _loggerFactory = loggerFactory;

        _queueTimer = new Timer(
                OnTimerCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Registers event handler.
    /// </summary>
    /// <returns>Task.</returns>
    public Task RunAsync()
    {
        if (Plugin.Instance!.Configuration.AutomaticAnalysis)
        {
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemModified;
            _taskManager.TaskCompleted += OnLibraryRefresh;
        }

        FFmpegWrapper.Logger = _logger;

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    // Disclose source for inspiration
    // Implementation based on the principles of jellyfin-plugin-media-analyzer:
    // https://github.com/endrl/jellyfin-plugin-media-analyzer

    /// <summary>
    /// Library item was added.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Library item was modified.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemModified(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// TaskManager task ended.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
    private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var result = eventArgs.Result;

        if (result.Key != "RefreshLibrary")
        {
            return;
        }

        if (result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Start timer to debounce analyzing.
    /// </summary>
    private void StartTimer()
    {
        if (Plugin.Instance!.AnalyzerTaskIsRunning)
        {
           _analyzeAgain = true; // Items added during a scan will be included later.
        }
        else
        {
            _logger.LogInformation("Media Library changed, analyzis will start soon!");
            _queueTimer.Change(TimeSpan.FromMilliseconds(20000), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Wait for timer callback to be completed.
    /// </summary>
    private void OnTimerCallback(object? state)
    {
        try
        {
            PerformAnalysis();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PerformAnalysis");
        }
    }

    /// <summary>
    /// Wait for timer to be completed.
    /// </summary>
    private void PerformAnalysis()
    {
        _logger.LogInformation("Timer elapsed - start analyzing");
        Plugin.Instance!.AnalyzerTaskIsRunning = true;

        var progress = new Progress<double>();
        var cancellationToken = new CancellationToken(false);

        if (Plugin.Instance!.Configuration.DetectIntros)
        {
            var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                AnalysisMode.Introduction,
                _loggerFactory.CreateLogger<DetectIntrosAndCreditsTask>(),
                _loggerFactory,
                _libraryManager);

            baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken);
        }

        if (Plugin.Instance!.Configuration.DetectCredits)
        {
            var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                AnalysisMode.Credits,
                _loggerFactory.CreateLogger<DetectIntrosAndCreditsTask>(),
                _loggerFactory,
                _libraryManager);

            baseCreditAnalyzer.AnalyzeItems(progress, cancellationToken);
        }

        Plugin.Instance!.AnalyzerTaskIsRunning = false;

        // New item detected, start timer again
        if (_analyzeAgain)
        {
            _logger.LogInformation("Analyzing ended, but we need to analyze again!");
            _analyzeAgain = false;
            StartTimer();
        }
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="dispose">Dispose.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (!dispose)
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemModified;
            _taskManager.TaskCompleted -= OnLibraryRefresh;

            _queueTimer.Dispose();

            return;
        }
    }
}
