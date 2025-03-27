using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Timers;
using System.Management;
using Circul8Service.Collectors;
using Circul8Service.Utils;
using System.Reflection;
using System.Threading.Tasks;
using System.Configuration;

namespace Circul8Service
{
    /// <summary>
    /// Main service class for the Circul8 system monitoring service.
    /// Collects and aggregates system metrics including battery, memory, processor, disk usage, and system events.
    /// </summary>
    public partial class Circul8Service : ServiceBase, IDisposable
    {
        #region Constants
        private const string EventSource = "Circul8";
        private const string WmiServiceName = "Winmgmt";
        private int _eventLogCheckIntervalMs;
        private int _systemInfoIntervalHours;
        private int _collectionPeriodMs;
        private int _aggregationCount;
        private bool _wmiServiceAvailable;
        #endregion

        #region Configuration
        private readonly ServiceConfiguration _config;
        private string _baseDirectory;
        #endregion

        #region Collectors
        private  IInfoCollector<Battery.BatteryPerformance> _batteryCollector;
        private  IInfoCollector<Memory.MemoryPerformance> _memoryCollector;
        private  IInfoCollector<Processor.ProcessorPerformance> _processorCollector;
        private  IInfoCollector<Disk.DiskPerformance> _diskCollector;
        private  IInfoCollector<SystemInfo.SystemPerformance> _systemInfoCollector;
        private readonly PayloadAssembler _payloadAssembler;
        #endregion

        #region Timers
        private readonly Timer _collectionTimer;
        private Timer _eventLogTimer;
        private Timer _systemInfoTimer;
        #endregion

        #region Collection State
        private readonly List<Battery.BatteryPerformance> _collectedBatteryMetrics = new List<Battery.BatteryPerformance>();
        private readonly List<Memory.MemoryPerformance> _collectedMemoryMetrics = new List<Memory.MemoryPerformance>();
        private readonly List<Processor.ProcessorPerformance> _collectedProcessorMetrics = new List<Processor.ProcessorPerformance>();
        private readonly List<Disk.DiskPerformance> _collectedDiskMetrics = new List<Disk.DiskPerformance>();
        private int _currentCollectionCount;
        #endregion

        #region Services
        private readonly InfluxDbManager _influxDbManager;
        private readonly EventLogService _eventLogService;
        #endregion

        #region IDisposable
        private bool _disposed;
        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the Circul8Service.
        /// </summary>
        public Circul8Service()
        {
            InitializeComponent();
            ServiceName = "Circul8";

            // Check WMI service availability first
            _wmiServiceAvailable = IsWmiServiceRunning();
            if (!_wmiServiceAvailable)
            {
                Logger.LogWarn("WMI service is not available. Some functionality may be limited.");
            }
            else
            {
                Logger.LogInfo("WMI service is available.");
            }

            // Initialize the logger with settings from config
            Logger.Initialize();

            // Create and configure our service configuration
            _config = new ServiceConfiguration();

            // Get command line arguments and process them
            string[] args = Environment.GetCommandLineArgs();
            _config.ProcessCommandLineArgs(args);

            // Load configuration values - use values from command line if specified
            _eventLogCheckIntervalMs = GetConfigValue("EventLogCheckIntervalMs", 24 * 60 * 60 * 1000); // 24 hours default
            _systemInfoIntervalHours = GetConfigValue("SystemInfoIntervalHours", 24);
            _collectionPeriodMs = _config.CollectionIntervalMs; // Use value from ServiceConfiguration
            _aggregationCount = _config.AggregationCount; // Use value from ServiceConfiguration

            // Override log level if specified in command line
            foreach (string arg in args)
            {
                if (arg.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
                {
                    string logLevel = arg.Substring("--log-level=".Length).Trim('"', '\'');
                    Logger.SetLogLevel(logLevel);
                }
            }

            _baseDirectory = _config.BaseDirectory;

            // Setup file paths
            InitializeFilePaths();

            // Initialize collectors based on configuration
            InitializeCollectors();

            _payloadAssembler = new PayloadAssembler();

            // Setup collection timer with configured period
            _collectionTimer = new System.Timers.Timer(_collectionPeriodMs) { AutoReset = true };
            _collectionTimer.Elapsed += CollectMetrics;

            // Initialize InfluxDB manager
            _influxDbManager = new InfluxDbManager();

            // Initialize event log service
            _eventLogService = new EventLogService(_baseDirectory);

            // Setup event log timer
            _eventLogTimer = new System.Timers.Timer(_eventLogCheckIntervalMs) { AutoReset = true };
            _eventLogTimer.Elapsed += CheckEventLogs;

            Logger.LogInfo($"Service initialized with collection period: {_collectionPeriodMs}ms, aggregation count: {_aggregationCount}");
        }

        private void InitializeCollectors()
        {
            Logger.LogInfo("Initializing collectors based on configuration...");

            if (_config.IsComponentEnabled("Battery") && Battery.IsBatteryPresent())
            {
                _batteryCollector = new Battery();
                Logger.LogInfo("Battery collector initialized");
            }
            else
            {
                Logger.LogInfo("Battery collector disabled or not present");
            }

            if (_config.IsComponentEnabled("Memory"))
            {
                _memoryCollector = new Memory();
                Logger.LogInfo("Memory collector initialized");
            }
            else
            {
                Logger.LogInfo("Memory collector disabled");
            }

            if (_config.IsComponentEnabled("Processor"))
            {
                _processorCollector = new Processor();
                Logger.LogInfo("Processor collector initialized");
            }
            else
            {
                Logger.LogInfo("Processor collector disabled");
            }

            if (_config.IsComponentEnabled("Disk"))
            {
                _diskCollector = new Disk();
                Logger.LogInfo("Disk collector initialized");
            }
            else
            {
                Logger.LogInfo("Disk collector disabled");
            }

            if (_config.IsComponentEnabled("SystemInfo"))
            {
                _systemInfoCollector = new Collectors.SystemInfo();
                _systemInfoTimer = new System.Timers.Timer { AutoReset = false };
                _systemInfoTimer.Elapsed += (sender, e) => Task.Run(async () => await CollectAndSendSystemInfoAsync());
                Logger.LogInfo("SystemInfo collector initialized");
            }
            else
            {
                Logger.LogInfo("SystemInfo collector disabled");
            }
        }

        private int GetConfigValue(string key, int defaultValue)
        {
            try
            {
                string value = ConfigurationManager.AppSettings[key];
                if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int result))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error reading configuration value for {key}: {ex.Message}");
            }
            return defaultValue;
        }

        #endregion

        #region Service Lifecycle Methods

        /// <summary>
        /// Called when the service starts.
        /// </summary>
        protected override void OnStart(string[] args)
        {
            try
            {
                InitializeServiceStartup();
            }
            catch (Exception ex)
            {
                HandleServiceStartupError(ex);
                Stop();
            }
        }

        private void InitializeServiceStartup()
        {
            // Read log level from configuration
            string configLogLevel = ConfigurationManager.AppSettings["LogLevel"];
            if (!string.IsNullOrEmpty(configLogLevel))
            {
                Logger.SetLogLevel(configLogLevel);
            }

            // Log basic service info
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Logger.LogInfo($"Circul8 service v{version} starting");

            // Log detailed configuration at debug level
            LogServiceConfiguration();

            // Enable service components
            EnsureEventLogSourceExists();

            // Configure event log checking interval
            ConfigureEventLogInterval();

            // Start timers
            StartServiceTimers();
        }

        private void LogServiceConfiguration()
        {
            Logger.LogDebug($"Executable path: {Assembly.GetExecutingAssembly().Location}");
            Logger.LogDebug($"Log path: {Logger.GetLogFilePath()}, Base directory: {_baseDirectory}");
            Logger.LogDebug($"Configuration: Collection period={_collectionPeriodMs}ms, Aggregation count={_aggregationCount}");

            if (Logger.ShouldLogMetricsDetails())
            {
                LogEnabledCollectors();
            }
        }

        private void LogEnabledCollectors()
        {
            var enabledCollectors = new List<string>();
            if (_batteryCollector != null) enabledCollectors.Add("Battery");
            if (_memoryCollector != null) enabledCollectors.Add("Memory");
            if (_processorCollector != null) enabledCollectors.Add("Processor");
            if (_diskCollector != null) enabledCollectors.Add("Disk");
            if (_systemInfoCollector != null) enabledCollectors.Add("SystemInfo");

            if (enabledCollectors.Count > 0)
            {
                Logger.LogDebug("Enabled collectors: " + string.Join(", ", enabledCollectors));
            }
            else
            {
                Logger.LogWarn("No collectors are enabled");
            }
        }

        private void ConfigureEventLogInterval()
        {
            string eventLogIntervalStr = ConfigurationManager.AppSettings["EventLogCheckIntervalMs"];
            if (!string.IsNullOrEmpty(eventLogIntervalStr) && int.TryParse(eventLogIntervalStr, out int eventLogInterval) && eventLogInterval > 0)
            {
                _eventLogCheckIntervalMs = eventLogInterval;
                Logger.LogDebug($"Event log check interval: {_eventLogCheckIntervalMs}ms");
            }
            else
            {
                _eventLogCheckIntervalMs = 24 * 60 * 60 * 1000; // 24 hours default
                Logger.LogDebug($"Using default event log check interval: {_eventLogCheckIntervalMs}ms");
            }
        }

        private void StartServiceTimers()
        {
            Logger.LogDebug("Starting collection timer");
            _collectionTimer.Start();

            if (_config.IsComponentEnabled("EventLogs"))
            {
                _eventLogTimer.Start();
                Logger.LogInfo($"Event log checking enabled (every {_eventLogCheckIntervalMs / 1000.0:F1} seconds)");

                // Do initial event log check
                Task.Run(async () => await ProcessEventLogs()).ConfigureAwait(false);
            }
            else
            {
                Logger.LogDebug("Event log checking disabled");
            }

            if (_config.IsComponentEnabled("SystemInfo"))
            {
                ScheduleInitialSystemInfoCollection();
            }
        }

        private void ScheduleInitialSystemInfoCollection()
        {
            Logger.LogDebug("SystemInfo collector enabled, scheduling immediate collection");

            Task.Run(async () => {
                try
                {
                    await Task.Delay(3000); // Small delay to ensure service is fully initialized
                    await CollectAndSendSystemInfoAsync();
                    ScheduleNextSystemInfoCollection();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error in immediate SystemInfo collection: {ex.Message}");
                }
            });
        }

        private void HandleServiceStartupError(Exception ex)
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "Circul8", "logs");
                Directory.CreateDirectory(logPath);
                File.AppendAllText(Path.Combine(logPath, "service_error.log"),
                    $"{DateTime.Now}: Error starting service: {ex.Message}\n{ex.StackTrace}\n");

                try
                {
                    System.Diagnostics.EventLog.WriteEntry("Circul8",
                        $"Error starting service: {ex.Message}", EventLogEntryType.Error);
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Called when the service stops.
        /// </summary>
        protected override void OnStop()
        {
            try
            {
                Logger.LogInfo("Circul8 service stopping");

                // Stop all timers
                StopServiceTimers();

                // Process any remaining metrics before stopping
                ProcessRemainingMetrics();

                // Log the reason for stopping
                LogServiceStopReason();

                Logger.LogInfo("Circul8 service has stopped");
            }
            catch (Exception ex)
            {
                HandleServiceStopError(ex);
            }
        }

        private void StopServiceTimers()
        {
            Logger.LogDebug("Stopping collection timer");
            _collectionTimer.Stop();

            Logger.LogDebug("Stopping event log timer");
            _eventLogTimer.Stop();

            if (_systemInfoTimer != null)
            {
                Logger.LogDebug("Stopping system info timer");
                _systemInfoTimer.Stop();
            }
        }

        private void ProcessRemainingMetrics()
        {
            if (_currentCollectionCount > 0 && (_collectedBatteryMetrics.Count > 0 || _collectedMemoryMetrics.Count > 0 ||
                _collectedProcessorMetrics.Count > 0 || _collectedDiskMetrics.Count > 0))
            {
                Logger.LogInfo("Processing remaining metrics before stopping");
                AggregateAndSendMetrics();
            }
        }

        private void LogServiceStopReason()
        {
            if (!_wmiServiceAvailable)
            {
                LogError("Service stopped because WMI service is not available");
            }
            else
            {
                Logger.LogInfo("Service stopped normally");
                System.Diagnostics.EventLog.WriteEntry(EventSource, "Circul8 service stopped", EventLogEntryType.Information);
            }
        }

        private void HandleServiceStopError(Exception ex)
        {
            try
            {
                Logger.LogError($"Error during service shutdown: {ex.Message}");
            }
            catch
            {
                try
                {
                    string logPath = Path.Combine(Path.GetTempPath(), "Circul8", "logs");
                    Directory.CreateDirectory(logPath);
                    File.AppendAllText(Path.Combine(logPath, "service_error.log"),
                        $"{DateTime.Now}: Error during service shutdown: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
            }
        }

        #endregion

        #region Metrics Collection

        /// <summary>
        /// Collects metrics from system services.
        /// </summary>
        private void CollectMetrics(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug($"Starting metrics collection at {DateTime.Now}");
                }

                _collectionTimer.Stop();

                CollectMetricsFromEnabledCollectors();

                _currentCollectionCount++;

                if (_currentCollectionCount >= _aggregationCount)
                {
                    AggregateAndSendMetrics();
                    _currentCollectionCount = 0;
                }
            }
            catch (Exception ex)
            {
                LogError($"Error in metrics collection: {ex.Message}");
            }
            finally
            {
                _collectionTimer.Start();
            }
        }

        private void CollectMetricsFromEnabledCollectors()
        {
            if (_batteryCollector != null)
            {
                CollectBatteryMetrics();
            }

            if (_memoryCollector != null)
            {
                CollectMemoryMetrics();
            }

            if (_processorCollector != null)
            {
                CollectProcessorMetrics();
            }

            if (_diskCollector != null)
            {
                CollectDiskMetrics();
            }
        }

        private void CollectBatteryMetrics()
        {
            try
            {
                var batteryMetrics = _batteryCollector.GetMetrics();
                _collectedBatteryMetrics.Add(batteryMetrics);
            }
            catch (Exception ex)
            {
                LogError($"Error collecting battery metrics: {ex.Message}");
            }
        }

        private void CollectMemoryMetrics()
        {
            try
            {
                var memoryMetrics = _memoryCollector.GetMetrics();
                _collectedMemoryMetrics.Add(memoryMetrics);
            }
            catch (Exception ex)
            {
                LogError($"Error collecting memory metrics: {ex.Message}");
            }
        }

        private void CollectProcessorMetrics()
        {
            try
            {
                var processorMetrics = _processorCollector.GetMetrics();
                _collectedProcessorMetrics.Add(processorMetrics);
            }
            catch (Exception ex)
            {
                LogError($"Error collecting processor metrics: {ex.Message}");
            }
        }

        private void CollectDiskMetrics()
        {
            try
            {
                var diskMetrics = _diskCollector.GetMetrics();
                _collectedDiskMetrics.Add(diskMetrics);
            }
            catch (Exception ex)
            {
                LogError($"Error collecting disk metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Aggregates and sends collected metrics.
        /// </summary>
        private void AggregateAndSendMetrics()
        {
            try
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug("Beginning metrics aggregation and sending process");
                }

                if (_collectedBatteryMetrics.Count > 0 || _collectedMemoryMetrics.Count > 0 ||
                    _collectedProcessorMetrics.Count > 0 || _collectedDiskMetrics.Count > 0)
                {
                    var aggregatedMetrics = AggregateMetrics();

                    if (Utils.Logger.ShouldLogMetricsDetails())
                    {
                        Logger.LogDebug("Clearing collected metrics after aggregation");
                    }
                    ClearCollectedMetrics();

                    if (Utils.Logger.ShouldLogMetricsDetails())
                    {
                        Logger.LogDebug("Assembling InfluxDB payload");
                    }
                    List<object> influxPayload = _payloadAssembler.AssembleInfluxDbPayload(
                        aggregatedMetrics.battery,
                        aggregatedMetrics.memory,
                        aggregatedMetrics.processor,
                        aggregatedMetrics.disk
                    );

                    LogAggregatedMetrics(aggregatedMetrics);

                    if (Utils.Logger.ShouldLogMetricsDetails())
                    {
                        Logger.LogDebug("Starting asynchronous publishing of payload");
                    }
                    Task.Run(async () => await PublishPayload(influxPayload));
                }
                else
                {
                    if (Utils.Logger.ShouldLogMetricsDetails())
                    {
                        Logger.LogDebug("No metrics collected during this period, nothing to aggregate");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error aggregating and sending metrics: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Logger.LogDebug($"Stack trace: {ex.StackTrace}");
                }
            }
        }

        private (Battery.BatteryPerformance battery, Memory.MemoryPerformance memory, 
                Processor.ProcessorPerformance processor, Disk.DiskPerformance disk) AggregateMetrics()
        {
            Battery.BatteryPerformance aggregatedBattery = null;
            Memory.MemoryPerformance aggregatedMemory = null;
            Processor.ProcessorPerformance aggregatedProcessor = null;
            Disk.DiskPerformance aggregatedDisk = null;

            if (_collectedBatteryMetrics.Count > 0)
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug($"Aggregating {_collectedBatteryMetrics.Count} battery metrics");
                }
                aggregatedBattery = AggregateBatteryMetrics(_collectedBatteryMetrics);
            }

            if (_collectedMemoryMetrics.Count > 0)
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug($"Aggregating {_collectedMemoryMetrics.Count} memory metrics");
                }
                aggregatedMemory = AggregateMemoryMetrics(_collectedMemoryMetrics);
            }

            if (_collectedProcessorMetrics.Count > 0)
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug($"Aggregating {_collectedProcessorMetrics.Count} processor metrics");
                }
                aggregatedProcessor = AggregateProcessorMetrics(_collectedProcessorMetrics);
            }

            if (_collectedDiskMetrics.Count > 0)
            {
                if (Utils.Logger.ShouldLogMetricsDetails())
                {
                    Logger.LogDebug($"Aggregating {_collectedDiskMetrics.Count} disk metrics");
                }
                aggregatedDisk = AggregateDiskMetrics(_collectedDiskMetrics);
            }

            return (aggregatedBattery, aggregatedMemory, aggregatedProcessor, aggregatedDisk);
        }

        private void ClearCollectedMetrics()
        {
            _collectedBatteryMetrics.Clear();
            _collectedMemoryMetrics.Clear();
            _collectedProcessorMetrics.Clear();
            _collectedDiskMetrics.Clear();
            _currentCollectionCount = 0;
        }

        private void LogAggregatedMetrics((Battery.BatteryPerformance battery, Memory.MemoryPerformance memory, 
            Processor.ProcessorPerformance processor, Disk.DiskPerformance disk) metrics)
        {
            if (metrics.battery != null)
            {
                Logger.LogInfo($"Aggregated battery metrics - Charge: {metrics.battery.ChargePercentage}%, Cycle Count: {metrics.battery.CycleCount}");
            }

            if (metrics.memory != null)
            {
                Logger.LogInfo($"Aggregated memory metrics - Available: {metrics.memory.MemoryAvailableMBytes}MB, Usage: {metrics.memory.MemoryUsagePercentage}%");
            }

            if (metrics.processor != null)
            {
                Logger.LogInfo($"Aggregated processor metrics - CPU Usage: {metrics.processor.ProcessorTime}%, User Mode: {metrics.processor.CpuUserModeTime}%, Processor Utility: {metrics.processor.CpuProcessorUtility}%");
            }

            if (metrics.disk != null)
            {
                Logger.LogInfo($"Aggregated disk metrics - Space Usage: {metrics.disk.DiskSpaceUsagePercentage:F2}% " +
                        $"(Total: {metrics.disk.TotalSizeGB:F2}GB, Used: {metrics.disk.UsedSpaceGB:F2}GB, Free: {metrics.disk.FreeSpaceGB:F2}GB), " +
                        $"I/O Usage: {metrics.disk.DiskTimePercentage:F2}%, " +
                        $"Queue Length: {metrics.disk.CurrentDiskQueueLength:F2}, " +
                        $"Read Queue: {metrics.disk.AvgDiskReadQueueLength:F2}, " +
                        $"Write Queue: {metrics.disk.AvgDiskWriteQueueLength:F2}");
            }
        }

        #endregion

        #region Metrics Aggregation

        /// <summary>
        /// Aggregates battery metrics from multiple samples.
        /// </summary>
        private Battery.BatteryPerformance AggregateBatteryMetrics(List<Battery.BatteryPerformance> metrics)
        {
            if (metrics == null || metrics.Count == 0)
                return null;

            var result = new Battery.BatteryPerformance();

            // Calculate averages for numeric values
            result.ChargePercentage = metrics.Average(m => m.ChargePercentage);
            result.Voltage = metrics.Average(m => m.Voltage);
            result.FullChargedCapacity = (long)metrics.Average(m => m.FullChargedCapacity);
            result.RemainingCapacity = (long)metrics.Average(m => m.RemainingCapacity);
            result.ChargeRate = metrics.Average(m => m.ChargeRate);
            result.DischargeRate = metrics.Average(m => m.DischargeRate);

            // For cycle count, use the most recent value since this shouldn't change during aggregation
            result.CycleCount = metrics.Last().CycleCount;

            // For charging states, use the most recent value instead of majority rule
            var lastMetric = metrics.Last();
            result.IsCharging = lastMetric.IsCharging;
            result.IsDischarging = lastMetric.IsDischarging;

            return result;
        }

        /// <summary>
        /// Aggregates memory metrics from multiple samples.
        /// </summary>
        private Memory.MemoryPerformance AggregateMemoryMetrics(List<Memory.MemoryPerformance> metrics)
        {
            if (metrics == null || metrics.Count == 0)
                return null;

            var mostRecent = metrics[metrics.Count - 1];
            return new Memory.MemoryPerformance
            {
                // Core Memory Metrics
                MemoryAvailableMBytes = metrics.Average(m => m.MemoryAvailableMBytes),
                CommittedBytesPercentage = metrics.Average(m => m.CommittedBytesPercentage),
                PoolNonPagedMB = metrics.Average(m => m.PoolNonPagedMB),
                PoolPagedMB = metrics.Average(m => m.PoolPagedMB),
                PageFaultsPersec = (long)metrics.Average(m => m.PageFaultsPersec),
                PageReadsPersec = (long)metrics.Average(m => m.PageReadsPersec),
                PagesPerSec = metrics.Average(m => m.PagesPerSec),
                CacheMB = metrics.Average(m => m.CacheMB),
                FreeSystemPageTableEntries = mostRecent.FreeSystemPageTableEntries,

                // Physical Memory Metrics
                PhysicalTotalGB = mostRecent.PhysicalTotalGB,
                PhysicalFreeGB = mostRecent.PhysicalFreeGB,
                PhysicalUsedGB = mostRecent.PhysicalUsedGB,
                MemoryUsagePercentage = metrics.Average(m => m.MemoryUsagePercentage)
            };
        }

        /// <summary>
        /// Aggregates processor metrics from multiple samples.
        /// </summary>
        private Processor.ProcessorPerformance AggregateProcessorMetrics(List<Processor.ProcessorPerformance> metrics)
        {
            if (metrics == null || metrics.Count == 0)
                return null;

            var result = new Processor.ProcessorPerformance();

            // Calculate averages for numeric values
            result.ProcessorTime = metrics.Average(m => m.ProcessorTime);
            result.CpuUserModeTime = metrics.Average(m => m.CpuUserModeTime);
            result.CpuInterruptTime = metrics.Average(m => m.CpuInterruptTime);
            result.CpuKernelModeTime = metrics.Average(m => m.CpuKernelModeTime);
            result.CpuIdleTimePercentage = metrics.Average(m => m.CpuIdleTimePercentage);
            result.ProcessorQueueLength = metrics.Average(m => m.ProcessorQueueLength);
            result.ContextSwitchesPerSec = metrics.Average(m => m.ContextSwitchesPerSec);
            result.CpuProcessorUtility = metrics.Average(m => m.CpuProcessorUtility);

            // Use the most recent value for non-averaged metrics
            var mostRecent = metrics.Last();
            result.ProcessorName = mostRecent.ProcessorName;
            result.Processes = mostRecent.Processes;
            result.Threads = mostRecent.Threads;
            result.SystemUpTime = mostRecent.SystemUpTime;

            return result;
        }

        /// <summary>
        /// Aggregates disk metrics from multiple samples.
        /// </summary>
        private Disk.DiskPerformance AggregateDiskMetrics(List<Disk.DiskPerformance> metrics)
        {
            if (metrics == null || metrics.Count == 0)
                return null;

            var result = new Disk.DiskPerformance();

            // Use the most recent sample's disk name (should be _Total)
            result.DiskName = metrics.Last().DiskName;

            // Calculate averages for numeric values
            result.CurrentDiskQueueLength = metrics.Average(m => m.CurrentDiskQueueLength);
            result.AvgDiskQueueLength = metrics.Average(m => m.AvgDiskQueueLength);
            result.AvgDiskReadQueueLength = metrics.Average(m => m.AvgDiskReadQueueLength);
            result.AvgDiskWriteQueueLength = metrics.Average(m => m.AvgDiskWriteQueueLength);
            result.AvgDiskSecPerRead = metrics.Average(m => m.AvgDiskSecPerRead);
            result.AvgDiskSecPerWrite = metrics.Average(m => m.AvgDiskSecPerWrite);
            result.PercentIdleTime = metrics.Average(m => m.PercentIdleTime);
            result.PercentDiskReadTime = metrics.Average(m => m.PercentDiskReadTime);
            result.PercentDiskWriteTime = metrics.Average(m => m.PercentDiskWriteTime);
            result.DiskTimePercentage = metrics.Average(m => m.DiskTimePercentage);

            // Use the most recent values for disk space metrics since they shouldn't change during aggregation
            var mostRecent = metrics.Last();
            result.TotalSizeGB = mostRecent.TotalSizeGB;
            result.FreeSpaceGB = mostRecent.FreeSpaceGB;
            result.UsedSpaceGB = mostRecent.UsedSpaceGB;
            result.DiskSpaceUsagePercentage = mostRecent.DiskSpaceUsagePercentage;

            return result;
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Sets up the file paths for the service, using temp directory for better reliability.
        /// </summary>
        private void InitializeFilePaths()
        {
            try
            {
                string configBaseDir = _baseDirectory;

                try
                {
                    if (!Directory.Exists(configBaseDir))
                    {
                        Directory.CreateDirectory(configBaseDir);
                    }
                }
                catch
                {
                    configBaseDir = Path.Combine(Path.GetTempPath(), "Circul8");
                    Logger.LogInfo($"Using temp directory as fallback: {configBaseDir}");
                }

                _baseDirectory = configBaseDir;
                EnsureDirectoryExists(_baseDirectory);

                string logsDir = Path.Combine(_baseDirectory, "logs");
                EnsureDirectoryExists(logsDir);

                Logger.LogInfo($"Using base directory: {_baseDirectory}");

                try
                {
                    System.Diagnostics.EventLog.WriteEntry(EventSource,
                        $"Using base directory: {_baseDirectory}",
                        EventLogEntryType.Information);
                }
                catch
                {
                    // Event log failed, already logged to file
                }
            }
            catch (Exception ex)
            {
                _baseDirectory = Path.Combine(Path.GetTempPath(), "Circul8");
                EnsureDirectoryExists(_baseDirectory);

                string errorMsg = $"Error initializing file paths: {ex.Message}";
                LogError(errorMsg);
                Logger.LogInfo($"Fallback to temp directory: {_baseDirectory}");
            }
        }

        /// <summary>
        /// Ensures a directory exists.
        /// </summary>
        private void EnsureDirectoryExists(string directory)
        {
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Logs an error message to the event log and a file.
        /// </summary>
        private void LogError(string message)
        {
            try
            {
                Utils.Logger.LogError(message);

                try
                {
                    System.Diagnostics.EventLog.WriteEntry(EventSource, message, EventLogEntryType.Error);
                }
                catch
                {
                    // Ignore event log failures to prevent recursion
                }
            }
            catch
            {
                // All logging failed - nothing more we can do
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Ensures the event log source exists.
        /// </summary>
        private void EnsureEventLogSourceExists()
        {
            if (!System.Diagnostics.EventLog.SourceExists(EventSource))
            {
                try
                {
                    System.Diagnostics.EventLog.CreateEventSource(EventSource, "Application");
                }
                catch (Exception)
                {
                    // Silently handle errors - we'll try to use file logging instead
                }
            }
        }

        /// <summary>
        /// Checks if the WMI service is running.
        /// </summary>
        private bool IsWmiServiceRunning()
        {
            try
            {
                using (ServiceController sc = new ServiceController(WmiServiceName))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public new void Dispose()
        {
            if (!_disposed)
            {
                _collectionTimer?.Dispose();
                _eventLogTimer?.Dispose();
                _systemInfoTimer?.Dispose();
                _eventLogService?.Dispose();

                if (_processorCollector is IDisposable disposableProcessor)
                {
                    disposableProcessor.Dispose();
                }

                _disposed = true;
                base.Dispose();
            }

            _influxDbManager?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Helper Classes and Methods

        public class ServiceConfiguration
        {
            private Dictionary<string, bool> _enabledCollectors;

            public int CollectionIntervalMs { get; private set; } = 3000; // 3 seconds default
            public int AggregationCount { get; private set; } = 5;
            public string BaseDirectory { get; private set; } = @"C:\ProgramData\Circul8";

            public ServiceConfiguration()
            {
                // Initialize all collectors as disabled by default
                _enabledCollectors = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["battery"] = false,
                    ["memory"] = false,
                    ["processor"] = false,
                    ["disk"] = false,
                    ["eventlogs"] = false,
                    ["systeminfo"] = false
                };

                // Load initial configuration from App.config
                LoadConfigurationFromAppConfig();
            }

            private void LoadConfigurationFromAppConfig()
            {
                try
                {
                    var config = ConfigurationManager.AppSettings;
                    
                    // Load collection interval from App.config
                    if (config["CollectionIntervalMs"] != null && int.TryParse(config["CollectionIntervalMs"], out int intervalMs))
                    {
                        CollectionIntervalMs = intervalMs;
                        Utils.Logger.LogInfo($"Loaded collection interval from config: {CollectionIntervalMs}ms");
                    }
                    
                    // Load aggregation count from App.config
                    if (config["MetricsAggregationCount"] != null && int.TryParse(config["MetricsAggregationCount"], out int aggCount))
                    {
                        AggregationCount = aggCount;
                        Utils.Logger.LogInfo($"Loaded aggregation count from config: {AggregationCount}");
                    }
                    
                    if (config["BaseDirectory"] != null)
                        BaseDirectory = config["BaseDirectory"];

                    // Load default enabled collectors from App.config
                    string defaultCollectors = config["DefaultEnabledCollectors"];
                    if (!string.IsNullOrEmpty(defaultCollectors))
                    {
                        string[] collectorList = defaultCollectors.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string collector in collectorList)
                        {
                            string normalizedName = collector.Trim().ToLower();
                            if (_enabledCollectors.ContainsKey(normalizedName))
                            {
                                _enabledCollectors[normalizedName] = true;
                                Utils.Logger.LogInfo($"Default enabled collector from config: {collector}");
                            }
                        }
                    }
                    else
                    {
                        // If no default collectors specified in config, enable all
                        foreach (var key in _enabledCollectors.Keys.ToList())
                        {
                            _enabledCollectors[key] = true;
                        }
                        Utils.Logger.LogInfo("No default collectors specified in config, enabling all collectors");
                    }
                }
                catch (Exception ex)
                {
                    Utils.Logger.LogError($"Error loading configuration from App.config: {ex.Message}");
                    // If there's an error, enable all collectors as fallback
                    foreach (var key in _enabledCollectors.Keys.ToList())
                    {
                        _enabledCollectors[key] = true;
                    }
                    Utils.Logger.LogInfo("Error in config, enabling all collectors as fallback");
                }
            }

            public void ProcessCommandLineArgs(string[] args)
            {
                if (args == null || args.Length == 0)
                    return;

                bool collectorSpecified = false;
                bool frequencySpecified = false;
                bool aggregationSpecified = false;

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i].ToLower();

                    try
                    {
                        if (arg.StartsWith("--collector_enabled="))
                        {
                            collectorSpecified = true;
                            // Reset all collectors to disabled first
                            foreach (var key in _enabledCollectors.Keys.ToList())
                            {
                                _enabledCollectors[key] = false;
                            }

                            string collectors = arg.Substring("--collector_enabled=".Length).Trim('"', '\'');
                            string[] collectorList = collectors.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (string collector in collectorList)
                            {
                                string normalizedName = collector.Trim().ToLower();
                                if (_enabledCollectors.ContainsKey(normalizedName))
                                {
                                    _enabledCollectors[normalizedName] = true;
                                    Utils.Logger.LogInfo($"Enabled collector from command line: {collector}");
                                }
                            }
                        }
                        else if (arg.StartsWith("--frequency="))
                        {
                            if (int.TryParse(arg.Substring("--frequency=".Length).Trim('"', '\''), out int seconds))
                            {
                                frequencySpecified = true;
                                CollectionIntervalMs = seconds * 1000;
                                Utils.Logger.LogInfo($"Set collection frequency from command line to {seconds} seconds ({CollectionIntervalMs}ms)");
                            }
                        }
                        else if (arg.StartsWith("--average_after="))
                        {
                            if (int.TryParse(arg.Substring("--average_after=".Length).Trim('"', '\''), out int count))
                            {
                                aggregationSpecified = true;
                                AggregationCount = count;
                                Utils.Logger.LogInfo($"Set aggregation count from command line to {count} samples");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Utils.Logger.LogError($"Error processing command line argument '{arg}': {ex.Message}");
                    }
                }

                // If settings were not specified in command line, they will keep the values loaded from App.config
                if (!frequencySpecified)
                {
                    Utils.Logger.LogInfo($"Using collection frequency from config: {CollectionIntervalMs}ms");
                }
                if (!aggregationSpecified)
                {
                    Utils.Logger.LogInfo($"Using aggregation count from config: {AggregationCount}");
                }
                if (!collectorSpecified)
                {
                    Utils.Logger.LogInfo("Using collector configuration from App.config");
                }

                // Log final configuration
                LogConfiguration();
            }

            private void LogConfiguration()
            {
                Utils.Logger.LogInfo("Service Configuration:");
                Utils.Logger.LogInfo($"Collection Interval: {CollectionIntervalMs}ms");
                Utils.Logger.LogInfo($"Aggregation Count: {AggregationCount}");
                Utils.Logger.LogInfo("Enabled Collectors: " + 
                    string.Join(", ", _enabledCollectors.Where(kvp => kvp.Value).Select(kvp => kvp.Key)));
            }

            public bool IsComponentEnabled(string componentName)
            {
                return _enabledCollectors.TryGetValue(componentName.ToLower(), out bool enabled) && enabled;
            }
        }

        private async Task<bool> PublishPayload(List<object> payload)
        {
            if (payload?.Count > 0)
            {
                try
                {
                    string enableInfluxDbValue = ConfigurationManager.AppSettings["EnableInfluxDb"];
                    bool enableInfluxDb = true;

                    if (!string.IsNullOrEmpty(enableInfluxDbValue) &&
                        bool.TryParse(enableInfluxDbValue, out bool enableValue))
                    {
                        enableInfluxDb = enableValue;
                    }

                    if (!enableInfluxDb)
                    {
                        Logger.LogDebug("InfluxDB publishing disabled, metrics not sent");
                        return false;
                    }

                    bool success = await _influxDbManager.WriteMetricsAsync(payload);

                    if (success)
                    {
                        Logger.LogInfo("Successfully published metrics to InfluxDB");
                        return true;
                    }
                    else
                    {
                        Logger.LogInfo("Failed to publish metrics to InfluxDB");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error publishing payload: {ex.Message}");
                    Logger.LogError($"Stack trace: {ex.StackTrace}");
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks for new event logs and publishes them to InfluxDB.
        /// </summary>
        private void CheckEventLogs(object sender, ElapsedEventArgs e)
        {
            try
            {
                _eventLogTimer.Stop();

                if (_eventLogService != null)
                {
                    _eventLogService.CollectEventLogs().Wait();
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing event logs: {ex.Message}");
            }
            finally
            {
                _eventLogTimer.Start();
            }
        }

        /// <summary>
        /// Processes event logs since the last check and publishes them to InfluxDB.
        /// </summary>
        private async Task ProcessEventLogs()
        {
            try
            {
                if (!_wmiServiceAvailable)
                {
                    Logger.LogInfo("WMI service is not available. Skipping event log check.");
                    return;
                }

                DateTime startTime = DateTime.Now;
                Logger.LogInfo("Checking for new event logs...");

                EventDetails eventDetails = await _eventLogService.CollectEventLogs();

                if (eventDetails.EventLogs != null && eventDetails.EventLogs.Count > 0)
                {
                    Logger.LogInfo($"Found {eventDetails.EventLogs.Count} new event logs.");

                    List<object> payload = new List<object>();
                    _payloadAssembler.AddEventLogsToPayload(eventDetails.EventLogs, payload);

                    if (payload.Count > 0)
                    {
                        await PublishPayload(payload);
                        Logger.LogInfo($"Event logs published successfully.");
                    }
                }
                else
                {
                    Logger.LogInfo("No new event logs found.");
                }

                TimeSpan elapsed = DateTime.Now - startTime;
                Logger.LogInfo($"Event log check completed in {elapsed.TotalSeconds:F2} seconds");

                if (elapsed.TotalSeconds > 10)
                {
                    int newInterval = Math.Min(_eventLogCheckIntervalMs * 2, 24 * 60 * 60 * 1000);
                    if (newInterval != _eventLogCheckIntervalMs)
                    {
                        Logger.LogInfo($"Event log check taking too long ({elapsed.TotalSeconds:F2}s), adjusting interval to {newInterval / 1000.0 / 60.0:F1} minutes");
                        UpdateEventLogCheckInterval(newInterval);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing event logs: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the event log check interval.
        /// </summary>
        public void UpdateEventLogCheckInterval(int intervalMs)
        {
            if (intervalMs <= 0)
            {
                LogError($"Invalid event log check interval: {intervalMs}. Must be greater than zero.");
                return;
            }

            _eventLogCheckIntervalMs = intervalMs;
            if (_eventLogTimer != null)
            {
                _eventLogTimer.Stop();
                _eventLogTimer.Interval = intervalMs;
                _eventLogTimer.Start();

                Logger.LogInfo($"Event log check interval updated to {intervalMs}ms ({intervalMs / 1000.0:F1} seconds)");
            }
        }

        /// <summary>
        /// Collects and sends system information
        /// </summary>
        private async Task CollectAndSendSystemInfoAsync()
        {
            try
            {
                Logger.LogInfo("Starting SystemInfo collection...");

                if (_systemInfoCollector == null)
                {
                    LogError("SystemInfo collector is null, cannot collect system information");
                    return;
                }

                var systemInfo = _systemInfoCollector.GetMetrics();
                if (systemInfo == null)
                {
                    LogError("Failed to collect system information, result was null");
                    return;
                }

                Logger.LogInfo($"SystemInfo collected successfully - OS: {systemInfo.OSName}, Processor: {systemInfo.ProcessorName}");

                await ProcessSystemInfoAsync(systemInfo);
                ScheduleNextSystemInfoCollection();

                Logger.LogInfo("SystemInfo collection and processing completed successfully");
            }
            catch (Exception ex)
            {
                LogError($"Error in CollectAndSendSystemInfoAsync: {ex.Message}");
                ScheduleNextSystemInfoCollection();
            }
        }

        /// <summary>
        /// Processes collected system information (saves and sends)
        /// </summary>
        private async Task ProcessSystemInfoAsync(SystemInfo.SystemPerformance systemInfo)
        {
            try
            {
                Logger.LogInfo("Processing collected system information");

                if (_influxDbManager != null)
                {
                    Logger.LogInfo("InfluxDB connected, assembling system info payload");
                    var payload = _payloadAssembler.AssembleSystemInfoPayload(systemInfo);

                    try
                    {
                        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
                        Logger.LogInfo($"System info payload: {jsonPayload.Substring(0, Math.Min(500, jsonPayload.Length))}...");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error serializing payload for logging: {ex.Message}");
                    }

                    Logger.LogInfo($"Publishing system info payload with {payload.Count} items");
                    bool success = await _influxDbManager.WriteMetricsAsync(payload);
                    Logger.LogInfo($"System information sent to cloud: {(success ? "Success" : "Failed")}");
                }
                else
                {
                    Logger.LogInfo("InfluxDB not connected, system info not sent");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing system information: {ex.Message}, Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Schedules the next system information collection
        /// </summary>
        private void ScheduleNextSystemInfoCollection()
        {
            int systemInfoIntervalHours = _systemInfoIntervalHours;
            _systemInfoTimer.Interval = TimeSpan.FromHours(systemInfoIntervalHours).TotalMilliseconds;
            _systemInfoTimer.Start();

            Logger.LogInfo($"Next system info collection scheduled in {systemInfoIntervalHours} hours");
        }

        #endregion
    }
}
