using System;
using System.Collections.Generic;
using System.Management;
using System.Diagnostics;
using Circul8Service.Collectors.Base;
using Circul8Service.Utils;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Collector for retrieving processor (CPU) performance metrics from Windows systems.
    /// Combines WMI and PerformanceCounter data for comprehensive CPU metrics.
    /// </summary>
    public class Processor : BaseCollector<Processor.ProcessorPerformance>, IDisposable
    {
        #region Constants and Fields
        
        private const string CIMV2_NAMESPACE = "root\\CIMV2";
        private PerformanceCounter _processorUtilityCounter;
        private bool _isProcessorUtilityCounterAvailable = false;
        private bool _disposed = false;
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// Initializes the Processor collector with the appropriate performance counters.
        /// Tries to use % Processor Utility first, with fallback to % Processor Time.
        /// </summary>
        public Processor()
        {
            try
            {
                // First try to initialize the modern counter
                _processorUtilityCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _processorUtilityCounter.NextValue();
                _isProcessorUtilityCounterAvailable = true;
                Logger.LogInfo("Successfully initialized Processor Utility performance counter");
            }
            catch (Exception ex)
            {
                _isProcessorUtilityCounterAvailable = false;
                Logger.LogInfo($"Modern processor counter unavailable: {ex.Message}. Trying legacy counter.");
                
                // Try Windows legacy counter as backup
                try 
                {
                    _processorUtilityCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _processorUtilityCounter.NextValue();
                    _isProcessorUtilityCounterAvailable = true;
                    Logger.LogInfo("Successfully initialized legacy Processor Time performance counter");
                }
                catch (Exception fallbackEx)
                {
                    Logger.LogError($"Failed to initialize processor counters: {fallbackEx.Message}. Using WMI only.");
                    _isProcessorUtilityCounterAvailable = false;
                    _processorUtilityCounter = null;
                }
            }
        }
        
        #endregion
        
        #region WMI Query Properties
        
        /// <summary>
        /// WMI query to retrieve processor performance data from performance counters
        /// </summary>
        protected override string WmiQuery => "SELECT Name, PercentProcessorTime, PercentUserTime, PercentInterruptTime, " +
                                      "PercentPrivilegedTime, PercentIdleTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'";
        
        /// <summary>
        /// WMI namespace for processor performance data
        /// </summary>
        protected override string WmiNamespace => CIMV2_NAMESPACE;
        
        #endregion
        
        #region Performance Metric Creation
        
        /// <summary>
        /// Creates a processor performance metric from a WMI object.
        /// Combines core processor metrics with system and performance counter data.
        /// </summary>
        /// <param name="obj">WMI object containing processor performance data</param>
        /// <returns>A populated ProcessorPerformance object</returns>
        protected override ProcessorPerformance CreatePerformanceMetric(ManagementObject obj)
        {
            try
            {
                var performance = new ProcessorPerformance();
                
                // Extract core processor metrics
                ExtractCoreProcessorMetrics(obj, performance);
                
                // Collect additional system metrics
                CollectSystemMetrics(performance);
                
                // Collect metrics from performance counters
                CollectPerformanceCounterMetrics(performance);

                return performance;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error creating processor performance metric: {ex.Message}", ex);
                return new ProcessorPerformance 
                { 
                    ProcessorName = "_Total",
                    ProcessorTime = 0,
                    CpuUserModeTime = 0,
                    CpuInterruptTime = 0,
                    CpuKernelModeTime = 0,
                    CpuIdleTimePercentage = 0,
                    ProcessorQueueLength = 0
                };
            }
        }
        
        /// <summary>
        /// Extracts core processor metrics from the WMI object.
        /// Gets processor time percentages for various metrics.
        /// </summary>
        /// <param name="obj">WMI object containing processor data</param>
        /// <param name="performance">Performance object to populate</param>
        private void ExtractCoreProcessorMetrics(ManagementObject obj, ProcessorPerformance performance)
        {
            try
            {
                // Basic processor metrics
                performance.ProcessorName = WmiHelper.ExtractStringValue(obj, "Name");
                performance.ProcessorTime = WmiHelper.ExtractDoubleValue(obj, "PercentProcessorTime");
                performance.CpuUserModeTime = WmiHelper.ExtractDoubleValue(obj, "PercentUserTime");
                performance.CpuInterruptTime = WmiHelper.ExtractDoubleValue(obj, "PercentInterruptTime");
                performance.CpuKernelModeTime = WmiHelper.ExtractDoubleValue(obj, "PercentPrivilegedTime");
                performance.CpuIdleTimePercentage = WmiHelper.ExtractDoubleValue(obj, "PercentIdleTime");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error extracting core processor metrics: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Collects additional system-level processor metrics such as processes, threads, and queue length.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectSystemMetrics(ProcessorPerformance performance)
        {
            try
            {
                const string query = "SELECT Processes, Threads, ContextSwitchesPersec, SystemUpTime, ProcessorQueueLength FROM Win32_PerfFormattedData_PerfOS_System";
                using (var searcher = new ManagementObjectSearcher(WmiNamespace, query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            performance.Processes = WmiHelper.ExtractIntValue(obj, "Processes");
                            performance.Threads = WmiHelper.ExtractIntValue(obj, "Threads");
                            performance.ContextSwitchesPerSec = WmiHelper.ExtractDoubleValue(obj, "ContextSwitchesPersec");
                            performance.SystemUpTime = WmiHelper.ExtractIntValue(obj, "SystemUpTime");
                            performance.ProcessorQueueLength = WmiHelper.ExtractDoubleValue(obj, "ProcessorQueueLength");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting system processor metrics: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Collects metrics from performance counters that aren't available through WMI.
        /// Gets the % Processor Utility which accounts for processor frequency scaling.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectPerformanceCounterMetrics(ProcessorPerformance performance)
        {
            try
            {
                // Get % Processor Utility if the counter is available
                if (_isProcessorUtilityCounterAvailable && _processorUtilityCounter != null)
                {
                    performance.CpuProcessorUtility = _processorUtilityCounter.NextValue();
                }
                else
                {
                    // Fall back to ProcessorTime if Processor Utility is not available
                    performance.CpuProcessorUtility = performance.ProcessorTime;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting performance counter metrics: {ex.Message}", ex);
                performance.CpuProcessorUtility = performance.ProcessorTime;
            }
        }
        
        /// <summary>
        /// Override GetMetrics to add extra error handling and fallback metrics.
        /// Ensures we never return null metrics even if collection fails.
        /// </summary>
        /// <returns>Processor performance metrics or a minimal fallback object</returns>
        public override ProcessorPerformance GetMetrics()
        {
            try
            {
                var metrics = base.GetMetrics();
                
                if (metrics == null)
                {
                    Logger.LogError("Failed to get processor metrics from WMI, creating fallback metrics");
                    // Return a minimal metrics object if the normal collection failed
                    metrics = new ProcessorPerformance
                    {
                        ProcessorName = "_Total",
                        ProcessorTime = 0, 
                        CpuUserModeTime = 0,
                        CpuProcessorUtility = 0
                    };
                }
                
                return metrics;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Critical error in GetMetrics: {ex.Message}", ex);
                // Return a minimal metrics object in case of error
                return new ProcessorPerformance 
                { 
                    ProcessorName = "_Total", 
                    ProcessorTime = 0, 
                    CpuProcessorUtility = 0 
                };
            }
        }
        
        #endregion
        
        #region IDisposable Implementation
        
        /// <summary>
        /// Cleans up resources when the collector is no longer needed.
        /// Disposes of any performance counters.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                if (_processorUtilityCounter != null)
                {
                    try
                    {
                        _processorUtilityCounter.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error disposing processor utility counter: {ex.Message}");
                    }
                    finally
                    {
                        _processorUtilityCounter = null;
                    }
                }
                
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }
        
        #endregion
        
        /// <summary>
        /// Represents processor performance metrics including CPU usage, idle time, and system metrics.
        /// </summary>
        public class ProcessorPerformance : Base.BasePerformance
        {
            #region Processor Metrics
            
            /// <summary>
            /// Name of the processor instance (typically "_Total" for all processors)
            /// </summary>
            public string ProcessorName { get; set; } = "_Total";
            
            /// <summary>
            /// Percentage of processor time spent in active use (0-100%)
            /// </summary>
            public double ProcessorTime { get; set; }
            
            /// <summary>
            /// Percentage of processor time spent in user mode (0-100%)
            /// </summary>
            public double CpuUserModeTime { get; set; }
            
            /// <summary>
            /// Percentage of processor time spent handling interrupts (0-100%)
            /// </summary>
            public double CpuInterruptTime { get; set; }
            
            /// <summary>
            /// Percentage of processor time spent in kernel (privileged) mode (0-100%)
            /// </summary>
            public double CpuKernelModeTime { get; set; }
            
            /// <summary>
            /// Percentage of processor time spent idle (0-100%)
            /// </summary>
            public double CpuIdleTimePercentage { get; set; }
            
            /// <summary>
            /// Number of threads in the processor queue waiting to be executed
            /// </summary>
            public double ProcessorQueueLength { get; set; }
            
            #endregion
            
            #region System Metrics
            
            /// <summary>
            /// Number of active processes in the system
            /// </summary>
            public int Processes { get; set; }
            
            /// <summary>
            /// Number of active threads in the system
            /// </summary>
            public int Threads { get; set; }
            
            /// <summary>
            /// Number of context switches per second
            /// </summary>
            public double ContextSwitchesPerSec { get; set; }
            
            /// <summary>
            /// Time in seconds since the system was last restarted
            /// </summary>
            public int SystemUpTime { get; set; }
            
            #endregion
            
            #region Performance Counter Metrics
            
            /// <summary>
            /// Percentage of processor utility (accounts for frequency scaling)
            /// This is more accurate than ProcessorTime on modern processors
            /// </summary>
            public double CpuProcessorUtility { get; set; }
            
            #endregion
        }
    }
}
