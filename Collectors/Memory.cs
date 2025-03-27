using System;
using System.Collections.Generic;
using System.Management;
using Circul8Service.Collectors.Base;
using Circul8Service.Utils;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Collector for retrieving memory performance metrics from Windows systems.
    /// Collects both physical memory information and performance counters.
    /// </summary>
    public class Memory : BaseCollector<Memory.MemoryPerformance>
    {
        #region Constants and Fields
        
        private const string CIMV2_NAMESPACE = "root\\CIMV2";
        private static long? _cachedTotalMemoryGB;
        
        #endregion
        
        #region WMI Query Properties
        
        /// <summary>
        /// WMI query to retrieve memory performance data from performance counters
        /// </summary>
        protected override string WmiQuery => "SELECT AvailableMBytes, PercentCommittedBytesInUse, " +
                                      "PoolNonpagedBytes, PoolPagedBytes, PageFaultsPersec, " +
                                      "PageReadsPersec, PagesPersec, CacheBytes, " + 
                                      "FreeSystemPageTableEntries " + 
                                      "FROM Win32_PerfFormattedData_PerfOS_Memory";

        /// <summary>
        /// WMI namespace for memory performance data
        /// </summary>
        protected override string WmiNamespace => CIMV2_NAMESPACE;
        
        #endregion
        
        #region Memory Information Methods
        
        /// <summary>
        /// Gets the total physical memory in GB with caching for performance.
        /// Uses WMI to query Win32_PhysicalMemory and calculates total capacity.
        /// </summary>
        /// <returns>Total memory in GB, or 0 if query fails</returns>
        public static long GetTotalMemoryGB()
        {
            // Return cached value if available
            if (_cachedTotalMemoryGB.HasValue)
            {
                return _cachedTotalMemoryGB.Value;
            }
            
            // Cache not available, query the memory
            long totalMemoryGB = 0;
            
            try
            {
                const string memoryQuery = "SELECT Capacity FROM Win32_PhysicalMemory";
                using (var searcher = new ManagementObjectSearcher(CIMV2_NAMESPACE, memoryQuery))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        try
                        {
                            using (item)
                            {
                                if (item["Capacity"] != null)
                                {
                                    totalMemoryGB += Convert.ToInt64(item["Capacity"]) / (1024 * 1024 * 1024);  // Convert bytes to GB
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error processing memory capacity item: {ex.Message}", ex);
                        }
                    }
                }
                
                // Cache the result for future calls
                _cachedTotalMemoryGB = totalMemoryGB;
                Logger.LogInfo($"Cached total memory: {totalMemoryGB} GB");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error querying physical memory: {ex.Message}", ex);
            }
            
            return totalMemoryGB;
        }
        
        #endregion
        
        #region Performance Metric Creation
        
        /// <summary>
        /// Creates a memory performance metric from a WMI object.
        /// Extracts counter data and calculates additional metrics.
        /// </summary>
        /// <param name="obj">WMI object containing memory performance data</param>
        /// <returns>A populated MemoryPerformance object</returns>
        protected override MemoryPerformance CreatePerformanceMetric(ManagementObject obj)
        {
            var performance = new MemoryPerformance();
            
            try
            {
                ExtractPerformanceCounters(obj, performance);
                CollectPhysicalMemoryInfo(performance);
                CalculateDerivedMetrics(performance);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error creating memory performance metric: {ex.Message}", ex);
            }
            
            return performance;
        }
        
        /// <summary>
        /// Extracts performance counter metrics from the WMI object.
        /// Handles unit conversion where needed (bytes to MB).
        /// </summary>
        /// <param name="obj">WMI object containing counter data</param>
        /// <param name="performance">Performance object to populate</param>
        private void ExtractPerformanceCounters(ManagementObject obj, MemoryPerformance performance)
        {
            try
            {
                // Basic memory metrics
                performance.MemoryAvailableMBytes = WmiHelper.ExtractDoubleValue(obj, "AvailableMBytes");
                performance.CommittedBytesPercentage = WmiHelper.ExtractDoubleValue(obj, "PercentCommittedBytesInUse");
                
                // Pool metrics - extract bytes and convert to MB
                long poolNonPagedBytes = WmiHelper.ExtractLongValue(obj, "PoolNonpagedBytes");
                long poolPagedBytes = WmiHelper.ExtractLongValue(obj, "PoolPagedBytes");
                performance.PoolNonPagedMB = poolNonPagedBytes / (1024.0 * 1024.0);
                performance.PoolPagedMB = poolPagedBytes / (1024.0 * 1024.0);
                
                // Page metrics
                performance.PageFaultsPersec = WmiHelper.ExtractLongValue(obj, "PageFaultsPersec");
                performance.PageReadsPersec = WmiHelper.ExtractLongValue(obj, "PageReadsPersec");
                performance.PagesPerSec = WmiHelper.ExtractDoubleValue(obj, "PagesPersec");
                
                // Cache metrics - extract bytes and convert to MB
                long cacheBytes = WmiHelper.ExtractLongValue(obj, "CacheBytes");
                performance.CacheMB = cacheBytes / (1024.0 * 1024.0);
                
                // Other memory metrics
                performance.FreeSystemPageTableEntries = WmiHelper.ExtractLongValue(obj, "FreeSystemPageTableEntries");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error extracting performance counters: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Collects physical memory information including total and free memory.
        /// Uses cached values for total memory to avoid repeated expensive queries.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectPhysicalMemoryInfo(MemoryPerformance performance)
        {
            try
            {
                // Get total physical memory
                performance.PhysicalTotalGB = GetTotalMemoryGB();
                
                // Get free physical memory using MemoryAvailableMBytes
                performance.PhysicalFreeGB = performance.MemoryAvailableMBytes / 1024;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting physical memory info: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Calculates derived metrics based on collected values.
        /// Handles memory usage percentage and used memory calculation.
        /// </summary>
        /// <param name="performance">Performance object to populate with calculated metrics</param>
        private void CalculateDerivedMetrics(MemoryPerformance performance)
        {
            try
            {
                // Calculate memory usage percentage 
                if (performance.PhysicalTotalGB > 0 && performance.PhysicalFreeGB >= 0)
                {
                    performance.MemoryUsagePercentage = 100 - (100.0 * performance.PhysicalFreeGB / performance.PhysicalTotalGB);
                    performance.PhysicalUsedGB = performance.PhysicalTotalGB - performance.PhysicalFreeGB;
                    
                    // Clamp to valid range (0-100%)
                    performance.MemoryUsagePercentage = Math.Max(0, Math.Min(100, performance.MemoryUsagePercentage));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error calculating derived memory metrics: {ex.Message}", ex);
            }
        }
        
        #endregion
        
        /// <summary>
        /// Represents memory performance metrics including usage, capacity, and system page information.
        /// </summary>
        public class MemoryPerformance : Base.BasePerformance
        {
            #region Core Memory Metrics
            
            /// <summary>
            /// Available physical memory in megabytes
            /// </summary>
            public double MemoryAvailableMBytes { get; set; }
            
            /// <summary>
            /// Percentage of committed bytes in use
            /// </summary>
            public double CommittedBytesPercentage { get; set; }
            
            /// <summary>
            /// Size of the nonpaged pool in megabytes
            /// </summary>
            public double PoolNonPagedMB { get; set; }
            
            /// <summary>
            /// Size of the paged pool in megabytes
            /// </summary>
            public double PoolPagedMB { get; set; }
            
            /// <summary>
            /// Rate of page faults per second
            /// </summary>
            public long PageFaultsPersec { get; set; }
            
            /// <summary>
            /// Rate of page reads per second
            /// </summary>
            public long PageReadsPersec { get; set; }
            
            /// <summary>
            /// Rate of pages per second
            /// </summary>
            public double PagesPerSec { get; set; }
            
            /// <summary>
            /// Size of the system cache in megabytes
            /// </summary>
            public double CacheMB { get; set; }
            
            /// <summary>
            /// Number of free system page table entries
            /// </summary>
            public long FreeSystemPageTableEntries { get; set; }
            
            #endregion
            
            #region Physical Memory Metrics
            
            /// <summary>
            /// Total physical memory in gigabytes
            /// </summary>
            public double PhysicalTotalGB { get; set; }
            
            /// <summary>
            /// Free physical memory in gigabytes
            /// </summary>
            public double PhysicalFreeGB { get; set; }
            
            /// <summary>
            /// Used physical memory in gigabytes
            /// </summary>
            public double PhysicalUsedGB { get; set; }
            
            /// <summary>
            /// Memory usage as a percentage (0-100%)
            /// </summary>
            public double MemoryUsagePercentage { get; set; }
            
            #endregion
            
        }
    }
} 