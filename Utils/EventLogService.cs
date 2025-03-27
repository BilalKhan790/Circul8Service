using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using System.Configuration;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using System.Text;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Service responsible for monitoring and processing Windows Event Logs
    /// </summary>
    /// <remarks>
    /// The EventLogService monitors critical system events that indicate application or system stability issues:
    /// 
    /// 1. Application Errors (Event ID 1000):
    ///    Indicates application crashes due to unhandled exceptions or memory access violations
    ///    
    /// 2. Application Hangs (Event ID 1002):
    ///    Tracks when applications become unresponsive and stop processing messages
    ///    
    /// 3. System Crashes/Bugcheck (Event ID 1001):
    ///    Captures information about system crashes (blue screens)
    ///    
    /// 4. Unexpected Shutdowns (Event ID 41):
    ///    Records when the system shuts down unexpectedly due to power loss or critical failures
    ///    
    /// This data helps identify patterns in system instability that may correlate with hardware performance issues.
    /// </remarks>
    public class EventLogService : IDisposable
    {
        private readonly SQLiteDataLayer _sqlDataLayer;
        private readonly string _baseDirectory;
        private readonly List<int> _allowedEventIds = new List<int> { 1000, 1001, 1002, 41 };

        /// <summary>
        /// Initializes a new instance of the EventLogService class.
        /// </summary>
        /// <param name="baseDirectory">Base directory for the SQLite database</param>
        public EventLogService(string baseDirectory)
        {
            Logger.LogInfo($"Initializing EventLogService with base directory: {baseDirectory}");
            
            _baseDirectory = baseDirectory;
            _sqlDataLayer = new SQLiteDataLayer(baseDirectory);
            
            Logger.LogInfo($"Monitoring {_allowedEventIds.Count} event IDs: {string.Join(", ", _allowedEventIds)}");
            
            int cleanedRecords = _sqlDataLayer.CleanupUnwantedEventIds(_allowedEventIds);
            Logger.LogInfo($"Initialization cleanup removed {cleanedRecords} unwanted event IDs");
            
            LogCurrentTimestamps();
            Logger.LogInfo("EventLogService initialization complete");
        }

        /// <summary>
        /// Collects Windows event logs with optimized performance
        /// </summary>
        /// <returns>EventDetails containing the event logs and last event timestamp</returns>
        public async Task<EventDetails> CollectEventLogs()
        {
            try
            {
                Logger.LogInfo("Starting Windows event log collection");
                
                if (_sqlDataLayer == null)
                {
                    Logger.LogError("SQLiteDataLayer is not initialized");
                    return new EventDetails { EventLogs = new List<EventLog>(), LastEventDateTime = DateTime.Now };
                }
                
                // Always use 24 hours ago as the fallback date
                DateTime fallbackDate = DateTime.Now.AddHours(-24);
                Logger.LogInfo($"Using default fallback date of 24 hours ago: {fallbackDate}");
                
                // Default to 24 hours lookback
                int defaultLookbackHours = 24;
                Logger.LogInfo($"Using default lookback period of {defaultLookbackHours} hours");
                
                // Define the specific event filters
                var eventFilters = new List<(int Code, string Source, string LogType)>
                {
                    (1000, "Application Error", "Application"),
                    (1002, "Application Hang", "Application"),
                    (1001, "Bugcheck", "System"),
                    (41, "Microsoft-Windows-Kernel-Power", "System")
                };
                
                Logger.LogDebug($"Monitoring {eventFilters.Count} event types: {string.Join(", ", eventFilters.Select(f => f.Code))}");
                
                List<EventLog> allEvents = new List<EventLog>();
                
                // Track latest times per event code
                Dictionary<int, DateTime> latestEventTimes = new Dictionary<int, DateTime>();
                foreach (var filter in eventFilters)
                {
                    latestEventTimes[filter.Code] = DateTime.MinValue;
                }
                
                // Ensure timestamps exist in database for all event types
                foreach (var eventCode in _allowedEventIds)
                {
                    try
                    {
                        DateTime storedTime = _sqlDataLayer.GetLastProcessedTimeForEvent(eventCode);
                        if (storedTime == DateTime.MinValue)
                        {
                            // If no timestamp exists in database, use the fallback date
                            if (!_sqlDataLayer.UpdateLastProcessedTimeForEvent(eventCode, fallbackDate))
                            {
                                Logger.LogError($"Failed to initialize event {eventCode} in database with fallback date");
                                continue;
                            }
                            Logger.LogInfo($"First run: Initialized event {eventCode} in database with fallback date: {fallbackDate}");
                        }
                        else
                        {
                            Logger.LogDebug($"Found existing timestamp for event {eventCode} in database: {storedTime}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error managing timestamp for event {eventCode}: {ex.Message}", ex);
                    }
                }
                
                // Batch process each log type to reduce WMI connections
                var logTypeGroups = eventFilters.GroupBy(f => f.LogType);
                
                foreach (var logGroup in logTypeGroups)
                {
                    string logType = logGroup.Key;
                    var codesInThisLog = logGroup.Select(g => g.Code).ToList();
                    var sourcesInThisLog = logGroup.Select(g => g.Source).Distinct().ToList();
                    
                    Logger.LogDebug($"Processing log type: {logType} with event codes: {string.Join(", ", codesInThisLog)}");
                    
                    // Find earliest timestamp for this log type to use in query
                    DateTime earliestCheckTime = DateTime.Now.AddHours(-defaultLookbackHours);
                    bool hasValidTimestamp = false;
                    
                    foreach (var filter in logGroup)
                    {
                        try
                        {
                            DateTime filterTime = _sqlDataLayer.GetLastProcessedTimeForEvent(filter.Code);
                            Logger.LogInfo($"Last processed time for event {filter.Code}: {filterTime}");
                            
                            if (filterTime != DateTime.MinValue && filterTime < earliestCheckTime)
                            {
                                earliestCheckTime = filterTime;
                                hasValidTimestamp = true;
                                Logger.LogDebug($"Updated earliest check time to {earliestCheckTime} based on event {filter.Code}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error retrieving timestamp for event {filter.Code}: {ex.Message}", ex);
                        }
                    }
                    
                    if (!hasValidTimestamp)
                    {
                        Logger.LogWarn($"No valid timestamps found for {logType} events, using default lookback time");
                    }
                    
                    // Format for WMI query
                    string formattedTime = earliestCheckTime.ToString("yyyyMMddHHmmss.ffffff") + "-000";
                    
                    // Create a more efficient query that covers all event codes in this log type
                    string codeList = string.Join(" OR EventCode = ", codesInThisLog);
                    string sourceList = string.Join("' OR SourceName = '", sourcesInThisLog);
                    
                    string query = $"SELECT * FROM Win32_NTLogEvent WHERE Logfile = '{logType}' " +
                                 $"AND (EventCode = {codeList}) " +
                                 $"AND EventType = 1 " +
                                 $"AND (SourceName = '{sourceList}') " +
                                 $"AND TimeGenerated >= '{formattedTime}'";
                    
                    Logger.LogInfo($"Querying {logType} events since {earliestCheckTime}");
                    Logger.LogDebug($"WMI query: {query}");
                    
                    try
                    {
                        // Execute one query per log type instead of per event code
                        await Task.Run(() => {
                            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
                            
                            // Set options to improve performance
                            searcher.Options.ReturnImmediately = true;  // Don't wait for results
                            searcher.Options.Rewindable = false;  // Forward-only access
                            
                            int eventCount = 0;
                            int processedCount = 0;
                            int skippedCount = 0;
                            int errorCount = 0;
                            
                            // Process results
                            foreach (ManagementObject eventObj in searcher.Get())
                            {
                                try
                                {
                                    eventCount++;
                                    
                                    // Get the event details
                                    string eventCode = eventObj["EventCode"]?.ToString();
                                    string sourceName = eventObj["SourceName"]?.ToString();
                                    string recordNumber = eventObj["RecordNumber"]?.ToString() ?? "";
                                    string timeGenStr = eventObj["TimeGenerated"]?.ToString();
                                    
                                    if (string.IsNullOrEmpty(eventCode) || string.IsNullOrEmpty(timeGenStr))
                                    {
                                        Logger.LogError($"Invalid event data: EventCode={eventCode}, TimeGenerated={timeGenStr}");
                                        errorCount++;
                                        continue;
                                    }
                                    
                                    // Find the matching filter
                                    var matchingFilter = logGroup.FirstOrDefault(f => 
                                        f.Code.ToString() == eventCode && f.Source == sourceName);
                                        
                                    if (matchingFilter.Code == 0)
                                    {
                                        Logger.LogDebug($"Skipping event with unmatched filter: Code={eventCode}, Source={sourceName}");
                                        skippedCount++;
                                        continue;
                                    }
                                    
                                    // Parse the time
                                    DateTime eventTime;
                                    try
                                    {
                                        eventTime = ManagementDateTimeConverter.ToDateTime(timeGenStr);
                                        if (eventTime == DateTime.MinValue)
                                        {
                                            Logger.LogError($"Failed to parse event time: {timeGenStr}");
                                            errorCount++;
                                            continue;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError($"Error parsing event time '{timeGenStr}': {ex.Message}");
                                        errorCount++;
                                        continue;
                                    }
                                    
                                    // Get current timestamp for this event ID
                                    DateTime lastCheckForThisEvent = _sqlDataLayer.GetLastProcessedTimeForEvent(matchingFilter.Code);
                                    
                                    // Only process if this event is newer than our last check
                                    if (eventTime > lastCheckForThisEvent)
                                    {
                                        Logger.LogDebug($"Found new event: ID={matchingFilter.Code}, Source={sourceName}, Time={eventTime}");
                                        
                                        // Add to our collection
                                        allEvents.Add(new EventLog
                                        {
                                            EventCode = eventCode,
                                            Message = eventObj["Message"]?.ToString() ?? "",
                                            SourceName = sourceName,
                                            Category = eventObj["Category"]?.ToString(),
                                            TimeGenerated = eventTime.ToString(),
                                            EventId = matchingFilter.Code.ToString()
                                        });
                                        
                                        // Track the latest event time for this specific event code
                                        if (eventTime > latestEventTimes[matchingFilter.Code])
                                        {
                                            latestEventTimes[matchingFilter.Code] = eventTime;
                                            Logger.LogDebug($"Updated latest time for event {matchingFilter.Code} to {eventTime}");
                                        }
                                        processedCount++;
                                    }
                                    else
                                    {
                                        Logger.LogDebug($"Skipping older/duplicate event: ID={matchingFilter.Code}, Time={eventTime}, Last check time={lastCheckForThisEvent}");
                                        skippedCount++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"Error processing event: {ex.Message}", ex);
                                    errorCount++;
                                }
                            }
                            
                            Logger.LogInfo($"Processed {eventCount} {logType} events: {processedCount} new, {skippedCount} skipped, {errorCount} errors");
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error executing query for {logType}: {ex.Message}", ex);
                    }
                }
                
                // If we found events, log them
                if (allEvents.Count > 0)
                {
                    Logger.LogInfo($"Found {allEvents.Count} new events to process");
                    
                    // Group events by event code/type for logging
                    var eventsByType = allEvents.GroupBy(e => e.EventCode);
                    foreach (var group in eventsByType)
                    {
                        Logger.LogInfo($"  - Event code {group.Key}: {group.Count()} events");
                    }
                    
                    int updateSuccessCount = 0;
                    int updateErrorCount = 0;
                    
                    foreach (var kvp in latestEventTimes)
                    {
                        int eventCode = kvp.Key;
                        DateTime latestTime = kvp.Value;
                        
                        if (latestTime > DateTime.MinValue)
                        {
                            Logger.LogInfo($"Updating last processed time for event {eventCode} in database to {latestTime}");
                            try
                            {
                                if (_sqlDataLayer.UpdateLastProcessedTimeForEvent(eventCode, latestTime))
                                {
                                    updateSuccessCount++;
                                }
                                else
                                {
                                    Logger.LogError($"Failed to update timestamp for event {eventCode}");
                                    updateErrorCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError($"Error updating timestamp for event {eventCode}: {ex.Message}", ex);
                                updateErrorCount++;
                            }
                        }
                    }
                    
                    if (updateErrorCount > 0)
                    {
                        Logger.LogError($"Failed to update {updateErrorCount} event timestamps");
                    }
                    Logger.LogInfo($"Successfully updated {updateSuccessCount} event timestamps");
                }
                else
                {
                    Logger.LogInfo("No new events found matching criteria");
                }
                
                // Get the latest time overall to return
                DateTime latestEventTimeOverall = latestEventTimes.Values.Max();
                if (latestEventTimeOverall > DateTime.MinValue)
                {
                    Logger.LogInfo($"Latest event time overall: {latestEventTimeOverall}");
                }
                
                Logger.LogInfo("Windows event log collection completed");
                
                return new EventDetails
                {
                    EventLogs = allEvents,
                    LastEventDateTime = latestEventTimeOverall > DateTime.MinValue ? latestEventTimeOverall : DateTime.Now
                };
            }
            catch (Exception ex)
            {
                Logger.LogError($"Critical error in event log collection: {ex.Message}", ex);
                return new EventDetails { EventLogs = new List<EventLog>(), LastEventDateTime = DateTime.Now };
            }
        }
        
        /// <summary>
        /// Cleans up any unwanted event IDs from the database
        /// </summary>
        /// <returns>Number of records removed</returns>
        public int CleanupUnwantedEventIds()
        {
            return _sqlDataLayer.CleanupUnwantedEventIds(_allowedEventIds);
        }
        
        /// <summary>
        /// Prints the current event IDs and timestamps in the database
        /// </summary>
        private void LogCurrentTimestamps()
        {
            Dictionary<int, DateTime> timestamps = _sqlDataLayer.GetAllEventTimestamps();
            Logger.LogInfo($"Current event timestamps in database:");
            foreach (var entry in timestamps)
            {
                Logger.LogInfo($"Event ID {entry.Key}: Last processed at {entry.Value}");
            }
        }
        
        /// <summary>
        /// Disposes resources
        /// </summary>
        public void Dispose()
        {
            _sqlDataLayer?.Dispose();
        }

        /// <summary>
        /// Resets the event log collection to start from a specific date
        /// </summary>
        /// <param name="startDate">The date to start collecting from</param>
        public void ResetEventLogCollection(DateTime startDate)
        {
            foreach (var eventId in _allowedEventIds)
            {
                _sqlDataLayer.UpdateLastProcessedTimeForEvent(eventId, startDate);
            }
            Logger.LogInfo($"Reset event log collection to start from {startDate}");
        }
    }

    /// <summary>
    /// Model class for event details
    /// </summary>
    public class EventDetails
    {
        public List<EventLog> EventLogs { get; set; }
        public DateTime LastEventDateTime { get; set; }
    }

    /// <summary>
    /// Model class for individual event log entries
    /// </summary>
    public class EventLog
    {
        public string EventCode { get; set; }
        public string Message { get; set; }
        public string SourceName { get; set; }
        public string Category { get; set; }
        public string TimeGenerated { get; set; }
        public string EventId { get; set; } // Unique identifier for deduplication
    }

    /// <summary>
    /// Helper class to convert WMI datetime format to .NET DateTime
    /// </summary>
    public static class ManagementDateTimeConverter
    {
        public static DateTime ToDateTime(string dmtfDate)
        {
            try
            {
                if (string.IsNullOrEmpty(dmtfDate))
                {
                    return DateTime.MinValue;
                }

                // WMI datetime format is: yyyyMMddHHmmss.ffffff+UUU
                int year = int.Parse(dmtfDate.Substring(0, 4));
                int month = int.Parse(dmtfDate.Substring(4, 2));
                int day = int.Parse(dmtfDate.Substring(6, 2));
                int hour = int.Parse(dmtfDate.Substring(8, 2));
                int minute = int.Parse(dmtfDate.Substring(10, 2));
                int second = int.Parse(dmtfDate.Substring(12, 2));

                return new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
} 
