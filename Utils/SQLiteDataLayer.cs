using System;
using System.Data.SQLite;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Provides data access layer functionality for SQLite database operations.
    /// Handles event log tracking and timestamp management.
    /// Implements IDisposable for proper resource management.
    /// </summary>
    public class SQLiteDataLayer : IDisposable
    {
        #region Constants
        private const string DATABASE_FILE = "EventLog.db";
        private const string TABLE_NAME = "EventLogs";
        #endregion

        #region Private Fields
        private readonly string _connectionString;
        private readonly SQLiteConnection _connection;
        private bool _disposed;
        private readonly string _databasePath;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new instance of the SQLiteDataLayer class and sets up the database.
        /// Establishes connection and ensures database structure is properly initialized.
        /// </summary>
        /// <exception cref="SQLiteException">Thrown when database initialization fails.</exception>
        public SQLiteDataLayer(string baseDirectory = null)
        {
            try
            {
                // Create database in the service base directory if specified, otherwise use current directory
                _databasePath = string.IsNullOrEmpty(baseDirectory) 
                    ? DATABASE_FILE 
                    : Path.Combine(baseDirectory, DATABASE_FILE);
                
                _connectionString = $"Data Source={_databasePath};Version=3;";
                _connection = new SQLiteConnection(_connectionString);
                InitializeDatabase();
                Logger.LogInfo($"SQLite database initialized at: {_databasePath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize SQLiteDataLayer: {ex.Message}");
                throw new SQLiteException($"Database initialization failed: {ex.Message}", ex);
            }
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Creates the necessary database tables if they don't exist.
        /// Establishes the initial database schema.
        /// </summary>
        /// <exception cref="SQLiteException">Thrown when table creation fails.</exception>
        private void InitializeDatabase()
        {
            try
            {
                Logger.LogInfo($"Initializing SQLite database at {_databasePath}");
                
                // Make sure database directory exists
                string directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    if (!Directory.Exists(directory))
                    {
                        Logger.LogInfo($"Creating database directory: {directory}");
                        try
                        {
                            Directory.CreateDirectory(directory);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Failed to create database directory {directory}: {ex.Message}", ex);
                            throw;
                        }
                    }
                }
                
                bool isNewDatabase = !File.Exists(_databasePath);
                
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    try
                    {
                        connection.Open();
                        Logger.LogInfo($"Successfully opened database connection to {_databasePath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to open database connection: {ex.Message}", ex);
                        throw;
                    }
                    
                    using (var command = connection.CreateCommand())
                    {
                        try
                        {
                            // Create table with an INTEGER EventID column
                            command.CommandText = $@"
                                CREATE TABLE IF NOT EXISTS {TABLE_NAME} (
                                    EventID INTEGER PRIMARY KEY,
                                    LastProcessedTimestamp DATETIME NOT NULL
                                );";
                            command.ExecuteNonQuery();
                            
                            if (isNewDatabase)
                            {
                                Logger.LogInfo("Created new event logs database table");
                            }
                            else
                            {
                                // Check if table exists and has correct schema
                                command.CommandText = $"PRAGMA table_info({TABLE_NAME});";
                                using (var reader = command.ExecuteReader())
                                {
                                    bool hasEventId = false;
                                    bool hasTimestamp = false;
                                    while (reader.Read())
                                    {
                                        string columnName = reader["name"].ToString();
                                        if (columnName == "EventID") hasEventId = true;
                                        if (columnName == "LastProcessedTimestamp") hasTimestamp = true;
                                    }
                                    
                                    if (!hasEventId || !hasTimestamp)
                                    {
                                        Logger.LogError($"Database table {TABLE_NAME} has incorrect schema");
                                        throw new SQLiteException($"Table {TABLE_NAME} is missing required columns");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Failed to create or verify database schema: {ex.Message}", ex);
                            throw;
                        }
                    }
                }
                
                Logger.LogInfo("Database initialization completed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Database initialization failed: {ex.Message}", ex);
                throw new SQLiteException($"Failed to create database schema: {ex.Message}", ex);
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Gets the last processed timestamp for a specific event ID
        /// </summary>
        /// <param name="eventId">The event ID to get the timestamp for</param>
        /// <returns>The timestamp, or DateTime.MinValue if not found</returns>
        public DateTime GetLastProcessedTimeForEvent(int eventId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $"SELECT LastProcessedTimestamp FROM {TABLE_NAME} WHERE EventID = @EventID";
                        command.Parameters.AddWithValue("@EventID", eventId);
                        
                        object result = command.ExecuteScalar();
                        DateTime timestamp = result != DBNull.Value && result != null 
                            ? Convert.ToDateTime(result) 
                            : DateTime.MinValue;
                            
                        Logger.LogDebug($"Retrieved timestamp for event ID {eventId}: {(timestamp == DateTime.MinValue ? "Not found" : timestamp.ToString())}");
                        return timestamp;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to get last processed time for event {eventId}: {ex.Message}");
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Gets the last processed time across all events
        /// </summary>
        /// <returns>The latest timestamp, or DateTime.MinValue if no records</returns>
        public DateTime GetLastProcessedTime()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $"SELECT MAX(LastProcessedTimestamp) FROM {TABLE_NAME}";
                        object result = command.ExecuteScalar();
                        DateTime timestamp = result != DBNull.Value ? Convert.ToDateTime(result) : DateTime.MinValue;
                        Logger.LogDebug($"Retrieved maximum timestamp across all events: {(timestamp == DateTime.MinValue ? "None" : timestamp.ToString())}");
                        return timestamp;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to get last processed time: {ex.Message}");
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Gets the last processed time for each event ID
        /// </summary>
        /// <returns>Dictionary with EventID as key and timestamp as value</returns>
        public Dictionary<int, DateTime> GetAllEventTimestamps()
        {
            Dictionary<int, DateTime> timestamps = new Dictionary<int, DateTime>();
            
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $"SELECT EventID, LastProcessedTimestamp FROM {TABLE_NAME}";
                        
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int eventId = Convert.ToInt32(reader["EventID"]);
                                DateTime timestamp = Convert.ToDateTime(reader["LastProcessedTimestamp"]);
                                timestamps[eventId] = timestamp;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to get all event timestamps: {ex.Message}");
            }
            
            return timestamps;
        }

        /// <summary>
        /// Removes any event IDs that aren't in the allowed list
        /// </summary>
        /// <param name="allowedEventIds">List of allowed event IDs</param>
        /// <returns>Number of records removed</returns>
        public int CleanupUnwantedEventIds(List<int> allowedEventIds)
        {
            try
            {
                if (allowedEventIds == null || allowedEventIds.Count == 0)
                {
                    Logger.LogError("Cannot cleanup with empty allowed event ID list");
                    return 0;
                }
                
                Logger.LogInfo($"Cleaning up unwanted event IDs. Allowed IDs: {string.Join(", ", allowedEventIds)}");
                
                // First, check what event IDs currently exist in the database
                Dictionary<int, DateTime> existingEvents = GetAllEventTimestamps();
                var unwantedIds = existingEvents.Keys.Where(id => !allowedEventIds.Contains(id)).ToList();
                
                if (unwantedIds.Count == 0)
                {
                    Logger.LogInfo("No unwanted event IDs found in database");
                    return 0;
                }
                
                Logger.LogInfo($"Found {unwantedIds.Count} unwanted event IDs to remove: {string.Join(", ", unwantedIds)}");
                
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        // Build a query that deletes any EventID not in the allowed list
                        string eventIdList = string.Join(",", allowedEventIds);
                        command.CommandText = $"DELETE FROM {TABLE_NAME} WHERE EventID NOT IN ({eventIdList})";
                        
                        int rowsAffected = command.ExecuteNonQuery();
                        if (rowsAffected > 0)
                        {
                            Logger.LogInfo($"Successfully removed {rowsAffected} unwanted event ID(s) from database");
                        }
                        else
                        {
                            Logger.LogInfo("No records were removed during cleanup");
                        }
                        return rowsAffected;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to cleanup unwanted event IDs: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Updates the last processed timestamp for a specific event ID
        /// </summary>
        /// <param name="eventId">The event ID to update</param>
        /// <param name="lastExecuted">The timestamp to record</param>
        /// <returns>True if successful</returns>
        public bool UpdateLastProcessedTimeForEvent(int eventId, DateTime lastExecuted)
        {
            try
            {
                // Check if this is a new record or an update
                bool isNewRecord = GetLastProcessedTimeForEvent(eventId) == DateTime.MinValue;
                Logger.LogDebug($"{(isNewRecord ? "Creating" : "Updating")} database record for event ID {eventId} with timestamp {lastExecuted}");
                
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        // Use INSERT OR REPLACE to handle both new and existing records
                        command.CommandText = $@"
                            INSERT OR REPLACE INTO {TABLE_NAME} (
                                EventID, 
                                LastProcessedTimestamp
                            ) VALUES (
                                @EventID,
                                @Timestamp
                            )";

                        command.Parameters.AddWithValue("@EventID", eventId);
                        command.Parameters.AddWithValue("@Timestamp", lastExecuted);
                        int rowsAffected = command.ExecuteNonQuery();
                        Logger.LogDebug($"Database operation for event ID {eventId} affected {rowsAffected} rows");
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to update last processed time for event {eventId}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Updates the last processed timestamp in the database
        /// </summary>
        /// <param name="lastExecuted">The timestamp to record</param>
        /// <returns>True if the update was successful, false otherwise</returns>
        public bool UpdateLastProcessedTime(DateTime lastExecuted)
        {
            try
            {
                // Instead of creating a new ID, this now updates the timestamps for all allowed event IDs
                List<int> allowedEventIds = new List<int> { 1000, 1001, 1002, 41 };
                bool success = true;
                
                foreach (int eventId in allowedEventIds)
                {
                    // Get the current timestamp for this event ID
                    DateTime currentTimestamp = GetLastProcessedTimeForEvent(eventId);
                    
                    // Only update if this timestamp is newer
                    if (lastExecuted > currentTimestamp)
                    {
                        if (!UpdateLastProcessedTimeForEvent(eventId, lastExecuted))
                        {
                            success = false;
                        }
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to update last processed time: {ex.Message}");
                return false;
            }
        }
        #endregion

        #region IDisposable Implementation
        /// <summary>
        /// Disposes of the SQLite connection and releases resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern.
        /// </summary>
        /// <param name="disposing">True if called from Dispose(), false if called from finalizer</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _connection?.Dispose();
            }

            _disposed = true;
        }
        #endregion
    }
} 