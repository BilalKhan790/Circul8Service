using System;
using System.Collections.Generic;
using System.Management;
using Circul8Service.Collectors.Base;
using Circul8Service.Utils;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Collector for retrieving disk performance metrics from the system
    /// </summary>
    public class Disk : BaseCollector<Disk.DiskPerformance>
    {
        /// <summary>
        /// WMI query to retrieve disk performance data for _Total disk only
        /// </summary>
        protected override string WmiQuery => "SELECT Name, CurrentDiskQueueLength, AvgDiskQueueLength, " +
                                   "AvgDiskReadQueueLength, AvgDiskWriteQueueLength, " +
                                   "AvgDiskSecPerRead, AvgDiskSecPerWrite, " +
                                   "PercentIdleTime, PercentDiskReadTime, PercentDiskWriteTime, " +
                                   "PercentDiskTime " +
                                   "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'";

        private const string DISK_SPACE_QUERY = "SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType=3";

        /// <summary>
        /// Creates a disk performance metric from a WMI object
        /// </summary>
        protected override DiskPerformance CreatePerformanceMetric(ManagementObject obj)
        {
            var performance = new DiskPerformance
            {
                DiskName = WmiHelper.ExtractStringValue(obj, "Name"),
                
                // Queue metrics
                CurrentDiskQueueLength = WmiHelper.ExtractDoubleValue(obj, "CurrentDiskQueueLength"),
                AvgDiskQueueLength = WmiHelper.ExtractDoubleValue(obj, "AvgDiskQueueLength"),
                AvgDiskReadQueueLength = WmiHelper.ExtractDoubleValue(obj, "AvgDiskReadQueueLength"),
                AvgDiskWriteQueueLength = WmiHelper.ExtractDoubleValue(obj, "AvgDiskWriteQueueLength"),
                
                // Read/Write latency metrics
                AvgDiskSecPerRead = WmiHelper.ExtractDoubleValue(obj, "AvgDiskSecPerRead"),
                AvgDiskSecPerWrite = WmiHelper.ExtractDoubleValue(obj, "AvgDiskSecPerWrite"),
                
                // Time percentage metrics
                PercentIdleTime = WmiHelper.ExtractDoubleValue(obj, "PercentIdleTime"),
                PercentDiskReadTime = WmiHelper.ExtractDoubleValue(obj, "PercentDiskReadTime"),
                PercentDiskWriteTime = WmiHelper.ExtractDoubleValue(obj, "PercentDiskWriteTime"),
                DiskTimePercentage = WmiHelper.ExtractDoubleValue(obj, "PercentDiskTime")
            };

            // Collect disk space information
            CollectDiskSpaceInfo(performance);
            
            return performance;
        }

        private void CollectDiskSpaceInfo(DiskPerformance performance)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(WmiNamespace, DISK_SPACE_QUERY))
                using (var results = searcher.Get())
                {
                    double totalSizeGB = 0;
                    double totalFreeSpaceGB = 0;

                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            ulong freeSpace = Convert.ToUInt64(obj["FreeSpace"]);
                            ulong totalSize = Convert.ToUInt64(obj["Size"]);

                            totalSizeGB += totalSize / (1024.0 * 1024.0 * 1024.0);
                            totalFreeSpaceGB += freeSpace / (1024.0 * 1024.0 * 1024.0);
                        }
                    }

                    double totalUsedSpaceGB = totalSizeGB - totalFreeSpaceGB;
                    double diskSpaceUsagePercentage = (totalUsedSpaceGB / totalSizeGB) * 100;

                    performance.TotalSizeGB = totalSizeGB;
                    performance.FreeSpaceGB = totalFreeSpaceGB;
                    performance.UsedSpaceGB = totalUsedSpaceGB;
                    performance.DiskSpaceUsagePercentage = diskSpaceUsagePercentage;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting disk space info: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Override to return the _Total disk data directly
        /// </summary>
        public override DiskPerformance GetMetrics()
        {
            var disks = GetInfo();
            return disks.Count > 0 ? disks[0] : new DiskPerformance { DiskName = "_Total" };
        }

        /// <summary>
        /// Represents disk performance metrics
        /// </summary>
        public class DiskPerformance : Base.BasePerformance
        {
            /// <summary>
            /// The name/identifier of the disk (e.g. "_Total", "0 C:")
            /// </summary>
            public string DiskName { get; set; } = string.Empty;
            
            /// <summary>
            /// The number of requests queued to the disk
            /// </summary>
            public double CurrentDiskQueueLength { get; set; }
            
            /// <summary>
            /// The average number of requests queued to the disk
            /// </summary>
            public double AvgDiskQueueLength { get; set; }
            
            /// <summary>
            /// The average number of read requests queued to the disk
            /// </summary>
            public double AvgDiskReadQueueLength { get; set; }
            
            /// <summary>
            /// The average number of write requests queued to the disk
            /// </summary>
            public double AvgDiskWriteQueueLength { get; set; }
            
            /// <summary>
            /// The average time, in seconds, to read data from the disk
            /// </summary>
            public double AvgDiskSecPerRead { get; set; }
            
            /// <summary>
            /// The average time, in seconds, to write data to the disk
            /// </summary>
            public double AvgDiskSecPerWrite { get; set; }
            
            /// <summary>
            /// Percentage of time the disk was idle
            /// </summary>
            public double PercentIdleTime { get; set; }
            
            /// <summary>
            /// Percentage of time the disk was busy servicing read requests
            /// </summary>
            public double PercentDiskReadTime { get; set; }
            
            /// <summary>
            /// Percentage of time the disk was busy servicing write requests
            /// </summary>
            public double PercentDiskWriteTime { get; set; }
            
            /// <summary>
            /// Percentage of time the disk was busy servicing requests
            /// </summary>
            public double DiskTimePercentage { get; set; }
            
            /// <summary>
            /// Total disk size in gigabytes across all drives
            /// </summary>
            public double TotalSizeGB { get; set; }
            
            /// <summary>
            /// Free space in gigabytes across all drives
            /// </summary>
            public double FreeSpaceGB { get; set; }
            
            /// <summary>
            /// Used space in gigabytes across all drives
            /// </summary>
            public double UsedSpaceGB { get; set; }
            
            /// <summary>
            /// Percentage of disk space used across all drives
            /// </summary>
            public double DiskSpaceUsagePercentage { get; set; }
        }
    }
} 