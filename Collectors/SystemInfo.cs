using System;
using System.Collections.Generic;
using System.Management;
using System.ServiceProcess;
using Circul8Service.Collectors.Base;
using Circul8Service.Utils;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Configuration;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Collector for retrieving system information metrics from the system
    /// </summary>
    public class SystemInfo : BaseCollector<SystemInfo.SystemPerformance>
    {
        /// <summary>
        /// WMI query to retrieve basic computer system information
        /// </summary>
        protected override string WmiQuery => "SELECT Manufacturer, Model, Name FROM Win32_ComputerSystem";
        protected override string WmiNamespace => "root\\CIMV2";

        /// <summary>
        /// Regex pattern for extracting power plan name
        /// </summary>
        private static readonly Regex PowerPlanRegex = new Regex(@"\*\s+([^(]+)\s+\(", RegexOptions.Compiled);

        /// <summary>
        /// Cache the system info as it doesn't change frequently
        /// </summary>
        private static SystemPerformance _cachedSystemInfo;
        private static DateTime _cacheTime = DateTime.MinValue;

        private const string ROOT_WMI = "ROOT\\WMI";

        /// <summary>
        /// Creates a system information metric from a WMI object
        /// </summary>
        /// <param name="obj">WMI object containing computer system data</param>
        /// <returns>A populated SystemPerformance object</returns>
        protected override SystemPerformance CreatePerformanceMetric(ManagementObject obj)
        {
            Logger.LogInfo("Creating SystemPerformance object from WMI data");
            var performance = new SystemPerformance
            {
                // Basic system info
                ComputerName = Environment.MachineName,
                Manufacturer = WmiHelper.ExtractStringValue(obj, "Manufacturer"),
                Model = WmiHelper.ExtractStringValue(obj, "Model"),
                
                // Collection time
                CollectionTime = DateTime.UtcNow
            };
            
            try
            {
                // Collect individual system components with better error handling
                Logger.LogInfo("Collecting OS info");
                CollectOperatingSystemInfo(performance);
                
                Logger.LogInfo("Collecting processor info");
                CollectProcessorInfo(performance);
                
                Logger.LogInfo("Collecting memory info");
                CollectMemoryInfo(performance);
                
                Logger.LogInfo("Collecting BIOS info");
                CollectBiosInfo(performance);
                
                Logger.LogInfo("Collecting disk drive info");
                CollectDiskDriveInfo(performance);
                
                Logger.LogInfo("Collecting network adapter info");
                CollectNetworkAdapterInfo(performance);
                
                Logger.LogInfo("SystemInfo collection completed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting system info components: {ex.Message}", ex);
            }
            
            return performance;
        }

        /// <summary>
        /// Override to use cached data when available
        /// </summary>
        public override List<SystemPerformance> GetInfo()
        {
            try
            {
                // Log the start of collection
                Logger.LogInfo("Starting SystemInfo collection");
                
                // Get interval from config
                int intervalHours = 24; // Default value
                if (int.TryParse(ConfigurationManager.AppSettings["SystemInfoIntervalHours"], 
                    out int configValue) && configValue > 0)
                {
                    intervalHours = configValue;
                }
                
                // Return cached system info if it's not too old
                if (_cachedSystemInfo != null)
                {
                    TimeSpan cacheAge = DateTime.UtcNow.Subtract(_cacheTime);
                    
                    if (cacheAge.TotalHours < intervalHours)
                    {
                        Logger.LogInfo("Using cached SystemInfo data");
                        return new List<SystemPerformance> { _cachedSystemInfo };
                    }
                }
                
                // If not cached or cache expired, get the info from WMI
                Logger.LogInfo("Cache expired or not available, collecting fresh SystemInfo data");
                var systemInfo = base.GetInfo();
                
                // Cache the result if successful
                if (systemInfo != null && systemInfo.Count > 0)
                {
                    _cachedSystemInfo = systemInfo[0];
                    _cacheTime = DateTime.UtcNow;
                    Logger.LogInfo("SystemInfo collection successful, data cached");
                }
                else
                {
                    Logger.LogError("SystemInfo collection returned no data");
                }
                
                return systemInfo;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in SystemInfo.GetInfo: {ex.Message}", ex);
                return new List<SystemPerformance>();
            }
        }

        /// <summary>
        /// Collects operating system information
        /// </summary>
        private void CollectOperatingSystemInfo(SystemPerformance performance)
        {
            try
            {
                var results = WmiHelper.ExecuteWmiQuery(
                    "SELECT Caption, Version, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem", 
                    WmiNamespace);
                    
                if (results != null)
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            performance.OSName = WmiHelper.ExtractStringValue(obj, "Caption");
                            performance.OSVersion = WmiHelper.ExtractStringValue(obj, "Version");
                            performance.OSArchitecture = WmiHelper.ExtractStringValue(obj, "OSArchitecture");
                            
                            // Parse dates as needed
                            string installDateStr = WmiHelper.ExtractStringValue(obj, "InstallDate");
                            if (!string.IsNullOrEmpty(installDateStr))
                            {
                                performance.OSInstallDate = Utils.ManagementDateTimeConverter.ToDateTime(installDateStr);
                            }
                            
                            string lastBootTimeStr = WmiHelper.ExtractStringValue(obj, "LastBootUpTime");
                            if (!string.IsNullOrEmpty(lastBootTimeStr))
                            {
                                performance.LastBootUpTime = Utils.ManagementDateTimeConverter.ToDateTime(lastBootTimeStr);
                            }
                            
                            break;
                        }
                    }
                }
                
                // Get active power plan
                performance.PowerPlanName = GetActivePowerPlanName();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting OS info: {ex.Message}", ex);
                performance.OSName = "Unknown (Error)";
                performance.OSVersion = "Unknown";
                performance.PowerPlanName = "Unknown";
            }
        }
        
        /// <summary>
        /// Gets active power plan using multiple methods for reliability
        /// </summary>
        private string GetActivePowerPlanName()
        {
            // Try WMI method first (most reliable on newer systems)
            string powerPlanName = GetActivePowerPlanNameWmi();
            
            // If WMI failed, try powercfg command as fallback
            if (string.IsNullOrEmpty(powerPlanName) || powerPlanName == "Unknown")
            {
                powerPlanName = GetActivePowerPlanNamePowercfg();
            }
            
            return !string.IsNullOrEmpty(powerPlanName) ? powerPlanName : "Unknown";
        }
        
        /// <summary>
        /// Gets active power plan using WMI query
        /// </summary>
        private string GetActivePowerPlanNameWmi()
        {
            try
            {
                string query = "SELECT ElementName FROM Win32_PowerPlan WHERE IsActive = True";
                
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2\power", query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            return WmiHelper.ExtractStringValue(obj, "ElementName");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting power plan via WMI: {ex.Message}", ex);
            }
            
            return "Unknown";
        }
        
        /// <summary>
        /// Gets active power plan using powercfg command as fallback method
        /// </summary>
        private string GetActivePowerPlanNamePowercfg()
        {
            try
            {
                // Configure process to run powercfg command
                ProcessStartInfo psi = new ProcessStartInfo("powercfg", "/list")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Execute the process and read its output
                using (Process process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        // Parse the output using regex to find the active power plan
                        var match = PowerPlanRegex.Match(output);
                        if (match.Success)
                        {
                            return match.Groups[1].Value.Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error retrieving the active power plan via powercfg: {ex.Message}", ex);
            }

            return "Unknown";
        }
        
        /// <summary>
        /// Collects processor information
        /// </summary>
        private void CollectProcessorInfo(SystemPerformance performance)
        {
            try
            {
                var results = WmiHelper.ExecuteWmiQuery(
                    "SELECT Name, Manufacturer, MaxClockSpeed, NumberOfCores, " +
                    "NumberOfLogicalProcessors, DeviceID FROM Win32_Processor", 
                    WmiNamespace);
                    
                if (results != null)
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            performance.ProcessorName = WmiHelper.ExtractStringValue(obj, "Name");
                            performance.ProcessorManufacturer = WmiHelper.ExtractStringValue(obj, "Manufacturer");
                            performance.ProcessorMaxClockSpeed = WmiHelper.ExtractIntValue(obj, "MaxClockSpeed");
                            performance.ProcessorCores = WmiHelper.ExtractIntValue(obj, "NumberOfCores");
                            performance.ProcessorLogicalProcessors = WmiHelper.ExtractIntValue(obj, "NumberOfLogicalProcessors");
                            
                            // Get DeviceID first
                            string deviceId = WmiHelper.ExtractStringValue(obj, "DeviceID");
                            performance.ProcessorId = deviceId;
                            
                            // Try to get ProcessorId in a separate query
                            try {
                                var idResults = WmiHelper.ExecuteWmiQuery(
                                    $"SELECT ProcessorId FROM Win32_Processor WHERE DeviceID='{deviceId}'", 
                                    WmiNamespace);
                                    
                                if (idResults != null)
                                {
                                    foreach (ManagementObject idObj in idResults)
                                    {
                                        using (idObj)
                                        {
                                            string processorId = WmiHelper.ExtractStringValue(idObj, "ProcessorId");
                                            if (!string.IsNullOrEmpty(processorId))
                                            {
                                                performance.ProcessorId = processorId;
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) {
                                Logger.LogError($"Error getting processor ID: {ex.Message}", ex);
                            }
                            
                            break; // Just get the first processor
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting processor info: {ex.Message}", ex);
                performance.ProcessorName = "Unknown (Error)";
                performance.ProcessorId = "Unknown";
            }
        }
        
        /// <summary>
        /// Collects memory information
        /// </summary>
        private void CollectMemoryInfo(SystemPerformance performance)
        {
            try
            {
                // Get total physical memory from Win32_PhysicalMemory
                var moduleResults = WmiHelper.ExecuteWmiQuery(
                    "SELECT Capacity FROM Win32_PhysicalMemory", 
                    WmiNamespace);
                    
                if (moduleResults != null)
                {
                    long totalCapacity = 0;
                    int moduleCount = 0;
                    
                    foreach (ManagementObject obj in moduleResults)
                    {
                        using (obj)
                        {
                            moduleCount++;
                            string capacityStr = WmiHelper.ExtractStringValue(obj, "Capacity");
                            if (!string.IsNullOrEmpty(capacityStr) && long.TryParse(capacityStr, out long capacity))
                            {
                                totalCapacity += capacity;
                            }
                        }
                    }
                    
                    // Capacity is in bytes, convert to GB
                    performance.TotalMemoryGB = Math.Round(totalCapacity / (1024.0 * 1024 * 1024), 2);
                    performance.MemoryModules = moduleCount;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting memory info: {ex.Message}", ex);
                performance.TotalMemoryGB = 0;
                performance.MemoryModules = 0;
            }
        }
        
        /// <summary>
        /// Collects BIOS information
        /// </summary>
        private void CollectBiosInfo(SystemPerformance performance)
        {
            try
            {
                var results = WmiHelper.ExecuteWmiQuery(
                    "SELECT Manufacturer, Name, Version, ReleaseDate FROM Win32_BIOS", 
                    WmiNamespace);
                    
                if (results != null)
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            performance.BiosManufacturer = WmiHelper.ExtractStringValue(obj, "Manufacturer");
                            performance.BiosName = WmiHelper.ExtractStringValue(obj, "Name");
                            performance.BiosVersion = WmiHelper.ExtractStringValue(obj, "Version");
                            
                            string releaseDateStr = WmiHelper.ExtractStringValue(obj, "ReleaseDate");
                            if (!string.IsNullOrEmpty(releaseDateStr))
                            {
                                performance.BiosReleaseDate = Utils.ManagementDateTimeConverter.ToDateTime(releaseDateStr);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting BIOS info: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Collects disk drive information
        /// </summary>
        private void CollectDiskDriveInfo(SystemPerformance performance)
        {
            try
            {
                int diskCount = 0;
                long totalSize = 0;
                
                var results = WmiHelper.ExecuteWmiQuery(
                    "SELECT Size FROM Win32_DiskDrive", 
                    WmiNamespace);
                    
                if (results != null)
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            diskCount++;
                            
                            // Try to get the size
                            string sizeStr = WmiHelper.ExtractStringValue(obj, "Size");
                            if (!string.IsNullOrEmpty(sizeStr) && long.TryParse(sizeStr, out long size))
                            {
                                totalSize += size;
                            }
                        }
                    }
                }
                
                performance.DiskDriveCount = diskCount;
                performance.TotalDiskSizeGB = totalSize / (1024.0 * 1024 * 1024);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting disk drive info: {ex.Message}", ex);
                performance.DiskDriveCount = 0;
                performance.TotalDiskSizeGB = 0;
            }
        }
        
        /// <summary>
        /// Collects network adapter information
        /// </summary>
        private void CollectNetworkAdapterInfo(SystemPerformance performance)
        {
            try
            {
                int adapterCount = 0;
                
                var results = WmiHelper.ExecuteWmiQuery(
                    "SELECT MACAddress FROM Win32_NetworkAdapter", 
                    WmiNamespace);
                    
                if (results != null)
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            // Only count adapters that have a MAC address (physical adapters)
                            string macAddress = WmiHelper.ExtractStringValue(obj, "MACAddress");
                            if (!string.IsNullOrEmpty(macAddress))
                            {
                                adapterCount++;
                            }
                        }
                    }
                }
                
                performance.NetworkAdapterCount = adapterCount;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting network adapter info: {ex.Message}", ex);
                performance.NetworkAdapterCount = 0;
            }
        }
        
        /// <summary>
        /// Override to customize the metrics collection behavior
        /// </summary>
        public override SystemPerformance GetMetrics()
        {
            try
            {
                // Log the start of collection
                Logger.LogInfo("Starting SystemInfo.GetMetrics");
                
                // Get interval from config
                int intervalHours = 24; // Default value
                try
                {
                    if (int.TryParse(ConfigurationManager.AppSettings["SystemInfoIntervalHours"], 
                        out int configValue) && configValue > 0)
                    {
                        intervalHours = configValue;
                        Logger.LogInfo($"Using SystemInfoIntervalHours from config: {intervalHours}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error reading SystemInfoIntervalHours from config: {ex.Message}", ex);
                }
                    
                // Use cached data if it's not too old
                if (_cachedSystemInfo != null)
                {
                    TimeSpan cacheAge = DateTime.Now - _cacheTime;
                    Logger.LogInfo($"Existing cache age: {cacheAge.TotalHours:F2} hours (validity: {intervalHours} hours)");
                        
                    if (cacheAge.TotalHours < intervalHours)
                    {
                        Logger.LogInfo("Using cached SystemInfo data");
                        return _cachedSystemInfo;
                    }
                    else
                    {
                        Logger.LogInfo("Cache expired, collecting fresh data");
                    }
                }
                else
                {
                    Logger.LogInfo("No cached data available, collecting fresh SystemInfo data");
                }

                // Get fresh data
                var metrics = GetInfo().FirstOrDefault();
                    
                if (metrics != null)
                {
                    // Update the cache
                    _cachedSystemInfo = metrics;
                    _cacheTime = DateTime.Now;
                    Logger.LogInfo($"Successfully collected SystemInfo data. OS: {metrics.OSName}, CPU: {metrics.ProcessorName}");
                        
                    return metrics;
                }
                else
                {
                    Logger.LogError("Failed to collect SystemInfo metrics - GetInfo returned null or empty");
                        
                    // If we had a previous cached value, use it even if expired
                    if (_cachedSystemInfo != null)
                    {
                        Logger.LogInfo("Using expired cached SystemInfo data as fallback");
                        return _cachedSystemInfo;
                    }
                        
                    // Otherwise, return an empty object
                    Logger.LogInfo("No cache available, returning empty SystemInfo object");
                    return new SystemPerformance 
                    { 
                        ComputerName = Environment.MachineName,
                        CollectionTime = DateTime.UtcNow,
                        OSName = "Unknown (Collection Failed)"
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in SystemInfo.GetMetrics: {ex.Message}", ex);
                    
                // Return empty object on error
                return new SystemPerformance 
                { 
                    ComputerName = Environment.MachineName,
                    CollectionTime = DateTime.UtcNow,
                    OSName = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Represents system information metrics
        /// </summary>
        public class SystemPerformance : Base.BasePerformance
        {
            #region Basic System Info
            /// <summary>
            /// Computer name/hostname
            /// </summary>
            public string ComputerName { get; set; }
            
            /// <summary>
            /// System manufacturer
            /// </summary>
            public string Manufacturer { get; set; }
            
            /// <summary>
            /// System model
            /// </summary>
            public string Model { get; set; }
            
            /// <summary>
            /// Time when this information was collected
            /// </summary>
            public DateTime CollectionTime { get; set; }
            #endregion
            
            #region Operating System
            /// <summary>
            /// Operating system name
            /// </summary>
            public string OSName { get; set; }
            
            /// <summary>
            /// Operating system version
            /// </summary>
            public string OSVersion { get; set; }
            
            /// <summary>
            /// Operating system architecture (32-bit or 64-bit)
            /// </summary>
            public string OSArchitecture { get; set; }
            
            /// <summary>
            /// Installation date of the operating system
            /// </summary>
            public DateTime OSInstallDate { get; set; }
            
            /// <summary>
            /// Last boot up time
            /// </summary>
            public DateTime LastBootUpTime { get; set; }
            
            /// <summary>
            /// Active power plan name
            /// </summary>
            public string PowerPlanName { get; set; }
            #endregion
            
            #region Processor
            /// <summary>
            /// Processor name
            /// </summary>
            public string ProcessorName { get; set; }
            
            /// <summary>
            /// Processor manufacturer
            /// </summary>
            public string ProcessorManufacturer { get; set; }
            
            /// <summary>
            /// Maximum clock speed in MHz
            /// </summary>
            public int ProcessorMaxClockSpeed { get; set; }
            
            /// <summary>
            /// Number of physical cores
            /// </summary>
            public int ProcessorCores { get; set; }
            
            /// <summary>
            /// Number of logical processors (threads)
            /// </summary>
            public int ProcessorLogicalProcessors { get; set; }
            
            /// <summary>
            /// Processor ID
            /// </summary>
            public string ProcessorId { get; set; }
            #endregion
            
            #region Memory
            /// <summary>
            /// Total physical memory in GB
            /// </summary>
            public double TotalMemoryGB { get; set; }
            
            /// <summary>
            /// Number of memory modules installed
            /// </summary>
            public int MemoryModules { get; set; }
            #endregion
            
            #region BIOS
            /// <summary>
            /// BIOS manufacturer
            /// </summary>
            public string BiosManufacturer { get; set; }
            
            /// <summary>
            /// BIOS name
            /// </summary>
            public string BiosName { get; set; }
            
            /// <summary>
            /// BIOS version
            /// </summary>
            public string BiosVersion { get; set; }
            
            /// <summary>
            /// BIOS release date
            /// </summary>
            public DateTime BiosReleaseDate { get; set; }
            #endregion
            
            #region Disk Drives
            /// <summary>
            /// Number of disk drives
            /// </summary>
            public int DiskDriveCount { get; set; }
            
            /// <summary>
            /// Total disk size in GB
            /// </summary>
            public double TotalDiskSizeGB { get; set; }
            #endregion
            
            #region Network
            /// <summary>
            /// Number of physical network adapters
            /// </summary>
            public int NetworkAdapterCount { get; set; }
            #endregion
        }
    }
}