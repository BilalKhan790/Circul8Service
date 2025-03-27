using System;
using Circul8Service.Collectors;
using System.Collections.Generic;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Assembles system performance metrics into structured payloads for InfluxDB
    /// </summary>
    public class PayloadAssembler
    {
        /// <summary>
        /// Assembles a payload for MQTT publishing in InfluxDB line protocol format
        /// </summary>
        public List<object> AssembleInfluxDbPayload(
            Battery.BatteryPerformance battery, 
            Memory.MemoryPerformance memory,
            Processor.ProcessorPerformance processor,
            Disk.DiskPerformance disk)
        {
            var payload = new List<object>();
            var collectionTime = DateTime.UtcNow;
            string deviceId = Environment.MachineName;
            
            // Battery data payload
            if (battery != null)
            {
                var batteryPayload = new
                {
                    measurement = "DevicePerformance",
                    tags = new
                    {
                        Device_id = deviceId,
                        Metrics_type = "Battery"
                    },
                    fields = new
                    {
                        ChargePercentage = battery.ChargePercentage,
                        IsCharging = battery.IsCharging ? 1 : 0,
                        IsDischarging = battery.IsDischarging ? 1 : 0,
                        CycleCount = battery.CycleCount,
                        FullChargedCapacity = battery.FullChargedCapacity,
                        RemainingCapacity = battery.RemainingCapacity,
                        ChargeRate = battery.ChargeRate,
                        DischargeRate = battery.DischargeRate,
                        Voltage = battery.Voltage,
                        EstimatedRemainingTime = battery.EstimatedRemainingTime,
                        HealthPercentage = battery.HealthPercentage
                    },
                    timestamp = ToUnixMilliseconds(collectionTime)
                };
                
                payload.Add(batteryPayload);
            }
            
            // Memory data payload
            if (memory != null)
            {
                var memoryPayload = new
                {
                    measurement = "DevicePerformance",
                    tags = new
                    {
                        Device_id = deviceId,
                        Metrics_type = "Memory"
                    },
                    fields = new
                    {
                        // Core Memory Metrics
                        MemoryAvailableMBytes = memory.MemoryAvailableMBytes,
                        CommittedBytesPercentage = memory.CommittedBytesPercentage,
                        PoolNonPagedMB = memory.PoolNonPagedMB,
                        PoolPagedMB = memory.PoolPagedMB,
                        PageFaultsPersec = memory.PageFaultsPersec,
                        PageReadsPersec = memory.PageReadsPersec,
                        PagesPerSec = memory.PagesPerSec,
                        CacheMB = memory.CacheMB,
                        FreeSystemPageTableEntries = memory.FreeSystemPageTableEntries,
                        
                        // Physical Memory Metrics
                        PhysicalTotalGB = memory.PhysicalTotalGB,
                        PhysicalFreeGB = memory.PhysicalFreeGB,
                        PhysicalUsedGB = memory.PhysicalUsedGB,
                        MemoryUsagePercentage = memory.MemoryUsagePercentage
                    },
                    timestamp = ToUnixMilliseconds(collectionTime)
                };
                
                payload.Add(memoryPayload);
            }
            
            // Processor data payload
            if (processor != null)
            {
                var processorPayload = new
                {
                    measurement = "DevicePerformance",
                    tags = new
                    {
                        Device_id = deviceId,
                        Metrics_type = "Processor"
                    },
                    fields = new
                    {
                        ProcessorName = processor.ProcessorName,
                        ProcessorTime = processor.ProcessorTime,
                        CpuUserModeTime = processor.CpuUserModeTime,
                        CpuInterruptTime = processor.CpuInterruptTime,
                        CpuKernelModeTime = processor.CpuKernelModeTime,
                        CpuIdleTimePercentage = processor.CpuIdleTimePercentage,
                        ProcessorQueueLength = processor.ProcessorQueueLength,
                        Processes = processor.Processes,
                        Threads = processor.Threads,
                        ContextSwitchesPerSec = processor.ContextSwitchesPerSec,
                        SystemUpTime = processor.SystemUpTime,
                        CpuProcessorUtility = processor.CpuProcessorUtility
                    },
                    timestamp = ToUnixMilliseconds(collectionTime)
                };
                
                payload.Add(processorPayload);
            }
            
            // Disk data payload
            if (disk != null)
            {
                var diskPayload = new
                {
                    measurement = "DevicePerformance",
                    tags = new
                    {
                        Device_id = deviceId,
                        Metrics_type = "Disk"
                    },
                    fields = new
                    {
                        DiskName = disk.DiskName,
                        CurrentDiskQueueLength = disk.CurrentDiskQueueLength,
                        AvgDiskQueueLength = disk.AvgDiskQueueLength,
                        AvgDiskReadQueueLength = disk.AvgDiskReadQueueLength,
                        AvgDiskWriteQueueLength = disk.AvgDiskWriteQueueLength,
                        AvgDiskSecPerRead = disk.AvgDiskSecPerRead,
                        AvgDiskSecPerWrite = disk.AvgDiskSecPerWrite,
                        PercentIdleTime = disk.PercentIdleTime,
                        PercentDiskReadTime = disk.PercentDiskReadTime,
                        PercentDiskWriteTime = disk.PercentDiskWriteTime,
                        DiskTimePercentage = disk.DiskTimePercentage,
                        TotalSizeGB = disk.TotalSizeGB,
                        FreeSpaceGB = disk.FreeSpaceGB,
                        UsedSpaceGB = disk.UsedSpaceGB,
                        DiskSpaceUsagePercentage = disk.DiskSpaceUsagePercentage
                    },
                    timestamp = ToUnixMilliseconds(collectionTime)
                };
                
                payload.Add(diskPayload);
            }
            
            return payload;
        }

        /// <summary>
        /// Converts DateTime to Unix timestamp in milliseconds
        /// </summary>
        private long ToUnixMilliseconds(DateTime dateTime)
        {
            return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Adds event logs to the payload for publishing to InfluxDB
        /// </summary>
        /// <remarks>
        /// Processes the following Windows event types:
        /// - Event 1000 (Application Error): Application crashes and exceptions
        /// - Event 1002 (Application Hang): Applications becoming unresponsive
        /// - Event 41 (Kernel-Power): Unexpected shutdowns or power loss events
        /// - Event 1001 (Bugcheck): System crash information (blue screens)
        /// 
        /// These events help understand application stability and system reliability.
        /// </remarks>
        public void AddEventLogsToPayload(List<EventLog> eventLogs, List<object> payload)
        {
            if (eventLogs == null || eventLogs.Count == 0)
            {
                return;
            }
            
            foreach (var eventLog in eventLogs)
            {
                try
                {
                    object fields = null;
                    long receivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    switch (Convert.ToInt32(eventLog.EventCode))
                    {
                        case 1000 when eventLog.SourceName == "Application Error":
                            fields = CreateApplicationErrorFields(eventLog, receivedTimestamp);
                            break;
                        case 1002 when eventLog.SourceName == "Application Hang":
                            fields = CreateApplicationHangFields(eventLog, receivedTimestamp);
                            break;
                        case 41 when eventLog.SourceName == "Microsoft-Windows-Kernel-Power":
                        case 1001 when eventLog.SourceName == "Bugcheck":
                            fields = CreateSystemEventFields(eventLog, receivedTimestamp);
                            break;
                    }

                    if (fields != null)
                    {
                        AddEventLogPayload(payload, eventLog, fields);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error processing event log: {ex.Message}");
                    continue;
                }
            }
        }

        private void AddEventLogPayload(List<object> payload, EventLog eventLog, object fields)
        {
            if (!DateTime.TryParse(eventLog.TimeGenerated, out DateTime eventDateTime))
            {
                eventDateTime = DateTime.Now;
            }

            var eventLogPayload = new
            {
                measurement = "DevicePerformance",
                tags = new
                {
                    Device_id = Environment.MachineName,
                    Metrics_type = DetermineMetricsType(eventLog),
                    EventID = eventLog.EventCode,
                    EventSource = eventLog.SourceName
                },
                fields = fields,
                timestamp = new DateTimeOffset(eventDateTime).ToUnixTimeMilliseconds(),
            };

            payload.Add(eventLogPayload);
        }

        private object CreateApplicationErrorFields(EventLog eventLog, long receivedTimestamp)
        {
            return new 
            {
                Message = eventLog.Message,
                TimeGenerated = eventLog.TimeGenerated,
                ApplicationName = ExtractDetail(eventLog.Message, "Faulting application name:"),
                ApplicationVersion = ExtractDetail(eventLog.Message, "version:", false),
                ModuleName = ExtractDetail(eventLog.Message, "Faulting module name:"),
                ModuleVersion = ExtractDetail(eventLog.Message, "version:", true),
                FaultAddress = ExtractDetail(eventLog.Message, "Fault offset:"),
                ReceivedTimestamp = receivedTimestamp
            };
        }

        private object CreateApplicationHangFields(EventLog eventLog, long receivedTimestamp)
        {
            var (programName, programVersion) = ExtractProgramNameAndVersion(eventLog.Message);
            return new 
            {
                Message = eventLog.Message,
                TimeGenerated = eventLog.TimeGenerated,
                ProgramName = programName ?? string.Empty,
                ProgramVersion = programVersion ?? string.Empty,
                ProcessID = ExtractDetail(eventLog.Message, "Process ID:"),
                StartTime = ExtractDetail(eventLog.Message, "Start Time:"),
                TerminationTime = ExtractDetail(eventLog.Message, "Termination Time:"),
                ApplicationPath = ExtractDetail(eventLog.Message, "Application Path:"),
                ReceivedTimestamp = receivedTimestamp
            };
        }

        private object CreateSystemEventFields(EventLog eventLog, long receivedTimestamp)
        {
            return new 
            {
                Message = eventLog.Message,
                TimeGenerated = eventLog.TimeGenerated,
                ReceivedTimestamp = receivedTimestamp
            };
        }

        private (string, string) ExtractProgramNameAndVersion(string message)
        {
            // Extract program name and version from hang message using regex
            var match = System.Text.RegularExpressions.Regex.Match(message, @"The program (.*?) version (.*?) stopped interacting");
            if (match.Success && match.Groups.Count >= 3)
            {
                return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
            }
            return (null, null);
        }

        private string ExtractDetail(string message, string key, bool lastOccurrence = false)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(key))
                return string.Empty;

            int index = lastOccurrence ? message.LastIndexOf(key) : message.IndexOf(key);
            if (index < 0) return string.Empty;

            int startIndex = index + key.Length;
            int endIndex = message.IndexOfAny(new[] { ',', '\n', '\r' }, startIndex);
            if (endIndex < 0) endIndex = message.Length;
            
            return message.Substring(startIndex, endIndex - startIndex).Trim();
        }

        private string DetermineMetricsType(EventLog eventLog)
        {
            int eventCode = Convert.ToInt32(eventLog.EventCode);
            return (eventCode, eventLog.SourceName) switch
            {
                (41, "Microsoft-Windows-Kernel-Power") => "KernelPower",
                (1001, "Bugcheck") => "SystemCrash",
                (1002, "Application Hang") => "AppHang",
                (1000, "Application Error") => "AppFault",
                _ => "EventLog"
            };
        }
        
        /// <summary>
        /// Assembles a payload for system information data
        /// </summary>
        public List<object> AssembleSystemInfoPayload(SystemInfo.SystemPerformance systemInfo)
        {
            var payload = new List<object>();
            string deviceId = Environment.MachineName;
            
            // System info payload
            var systemInfoPayload = new
            {
                  
                measurement = "DevicePerformance",
                tags = new
                {
                    Device_id = deviceId,
                    Metrics_type = "SystemInfo"
                },
                fields = new
                {
                    // Basic info
                    Manufacturer = systemInfo.Manufacturer,
                    Model = systemInfo.Model,                    
                    CSName = deviceId,

                    // OS info
                    OSName = systemInfo.OSName,
                    OSVersion = systemInfo.OSVersion,
                    OSArchitecture = systemInfo.OSArchitecture,
                    OSInstallDate = systemInfo.OSInstallDate.ToString("yyyy-MM-dd"),
                    LastBootUpTime = systemInfo.LastBootUpTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    PowerPlanName = systemInfo.PowerPlanName,
                    
                    // CPU
                    ProcessorName = systemInfo.ProcessorName,
                    ProcessorManufacturer = systemInfo.ProcessorManufacturer,
                    ProcessorCores = systemInfo.ProcessorCores,
                    ProcessorLogicalProcessors = systemInfo.ProcessorLogicalProcessors,
                    ProcessorMaxClockSpeed = systemInfo.ProcessorMaxClockSpeed,
                    ProcessorId = systemInfo.ProcessorId,
                    
                    // Memory
                    TotalMemoryGB = systemInfo.TotalMemoryGB,
                    MemoryModules = systemInfo.MemoryModules,
                    
                    // BIOS
                    BiosManufacturer = systemInfo.BiosManufacturer,
                    BiosName = systemInfo.BiosName,
                    BiosVersion = systemInfo.BiosVersion,
                    BiosReleaseDate = systemInfo.BiosReleaseDate.ToString("yyyy-MM-dd"),
                    
                    // Storage and Network
                    DiskDriveCount = systemInfo.DiskDriveCount,
                    TotalDiskSizeGB = systemInfo.TotalDiskSizeGB,
                    NetworkAdapterCount = systemInfo.NetworkAdapterCount
                },
                timestamp = ToUnixMilliseconds(systemInfo.CollectionTime)
            };
            
            payload.Add(systemInfoPayload);
            return payload;
        }
    }
} 