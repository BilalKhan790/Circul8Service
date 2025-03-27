using System;
using System.IO;
using System.Diagnostics;
using System.Configuration;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Static logger class for application-wide logging with configurable log levels
    /// </summary>
    public static class Logger
    {
        #region Private Fields
        // Core settings
        private static string _logDirectory;
        private static LogLevel _logLevel = LogLevel.Info;
        private static bool _initialized = false;
        private static string _currentLogFilePath;
        
        // Log file management
        private static int _maxLogFileSize = 10240; // 10MB default
        private static int _maxLogFiles = 5;
        
        // Warning suppression
        private static bool _suppressRepeatWarnings = true;
        private static Regex _warningPatternRegex = null;
        private static HashSet<string> _recentWarnings = new HashSet<string>();
        
        // Metrics logging
        private static bool _logMetricsCollectionDetails = false;
        private static bool _isLogging = false;
        #endregion

        #region Public Types
        /// <summary>
        /// Enum representing log levels with numeric values for comparison
        /// </summary>
        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3,
            None = 4
        }
        #endregion

        #region Initialization
        static Logger()
        {
            // Read log level from config before any initialization
            string configLogLevel = ConfigurationManager.AppSettings["LogLevel"];
            if (!string.IsNullOrEmpty(configLogLevel) && Enum.TryParse(configLogLevel, true, out LogLevel parsedLevel))
            {
                _logLevel = parsedLevel;
            }
            
            // Initialize log directory
            InitializeLogDirectory();
        }

        /// <summary>
        /// Initializes the logger with settings from app.config
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            // Initialize core settings
            InitializeSettings();
            
            _initialized = true;
            WriteLog("INFO", $"Logger initialized. Directory: {_logDirectory}, Level: {_logLevel}");
            
            // Manage log files
            CheckLogFileSize();
            CleanupOldLogFiles();
        }

        /// <summary>
        /// Initializes all logger settings from configuration
        /// </summary>
        private static void InitializeSettings()
        {
            // Core settings
            string configLogLevel = ConfigurationManager.AppSettings["LogLevel"];
            if (!string.IsNullOrEmpty(configLogLevel) && Enum.TryParse(configLogLevel, true, out LogLevel parsedLevel))
            {
                _logLevel = parsedLevel;
            }
            
            // Log file settings
            string maxLogFileSizeStr = ConfigurationManager.AppSettings["MaxLogFileSize"];
            if (!string.IsNullOrEmpty(maxLogFileSizeStr) && int.TryParse(maxLogFileSizeStr, out int maxSize))
            {
                _maxLogFileSize = maxSize;
            }
            
            string maxLogFilesStr = ConfigurationManager.AppSettings["MaxLogFiles"];
            if (!string.IsNullOrEmpty(maxLogFilesStr) && int.TryParse(maxLogFilesStr, out int maxFiles))
            {
                _maxLogFiles = maxFiles;
            }
            
            // Warning settings
            string suppressRepeatWarnings = ConfigurationManager.AppSettings["SuppressRepeatWarnings"];
            if (!string.IsNullOrEmpty(suppressRepeatWarnings))
            {
                bool.TryParse(suppressRepeatWarnings, out _suppressRepeatWarnings);
            }
            
            string suppressWarningPattern = ConfigurationManager.AppSettings["SuppressWarningPattern"];
            if (!string.IsNullOrEmpty(suppressWarningPattern))
            {
                try
                {
                    _warningPatternRegex = new Regex(suppressWarningPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch { /* If regex is invalid, just use string contains check */ }
            }
            
            // Metrics settings
            string logMetricsDetails = ConfigurationManager.AppSettings["LogMetricsCollectionDetails"];
            if (!string.IsNullOrEmpty(logMetricsDetails))
            {
                bool.TryParse(logMetricsDetails, out _logMetricsCollectionDetails);
            }
        }
        #endregion

        #region Log Directory Management
        /// <summary>
        /// Initializes the log directory from app.config or uses temp directory as fallback
        /// </summary>
        private static void InitializeLogDirectory()
        {
            try
            {
                // Try to use configured directory
                string baseDir = ConfigurationManager.AppSettings["BaseDirectory"];
                string logsDir = ConfigurationManager.AppSettings["LogsDirectory"];

                if (!string.IsNullOrEmpty(baseDir) && !string.IsNullOrEmpty(logsDir))
                {
                    _logDirectory = Path.Combine(baseDir, logsDir);
                }
                else
                {
                    // Fallback to temp directory
                    _logDirectory = Path.Combine(Path.GetTempPath(), "Circul8", "logs");
                }

                // Ensure directory exists and set log file path
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
                _currentLogFilePath = Path.Combine(_logDirectory, $"service_{DateTime.Now:yyyyMMdd}.log");
            }
            catch (Exception ex)
            {
                // Last resort fallback to temp directory
                try
                {
                    _logDirectory = Path.Combine(Path.GetTempPath(), "Circul8", "logs");
                    Directory.CreateDirectory(_logDirectory);
                    _currentLogFilePath = Path.Combine(_logDirectory, $"service_{DateTime.Now:yyyyMMdd}.log");
                    
                    // Log the error
                    File.AppendAllText(_currentLogFilePath, 
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [ERROR] - Failed to initialize log directory: {ex.Message}{Environment.NewLine}");
                }
                catch { /* Nothing more we can do */ }
            }
        }
        
        /// <summary>
        /// Checks if current log file exceeds max size and rotates if needed
        /// </summary>
        private static void CheckLogFileSize()
        {
            try
            {
                if (File.Exists(_currentLogFilePath))
                {
                    FileInfo fileInfo = new FileInfo(_currentLogFilePath);
                    long sizeKB = fileInfo.Length / 1024;
                    
                    if (sizeKB > _maxLogFileSize)
                    {
                        // Create a new log file with timestamp
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        _currentLogFilePath = Path.Combine(_logDirectory, $"service_{timestamp}.log");
                        
                        // Notify about rotation
                        File.AppendAllText(_currentLogFilePath, 
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [INFO] - Previous log file reached size limit ({sizeKB}KB > {_maxLogFileSize}KB), created new log file{Environment.NewLine}");
                    }
                }
            }
            catch { /* Ignore errors in log file rotation */ }
        }
        
        /// <summary>
        /// Cleans up old log files if they exceed the maximum count
        /// </summary>
        private static void CleanupOldLogFiles()
        {
            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, "service_*.log")
                    .OrderByDescending(f => f)
                    .ToList();
                
                if (logFiles.Count > _maxLogFiles)
                {
                    foreach (var file in logFiles.Skip(_maxLogFiles))
                    {
                        try
                        {
                            File.Delete(file);
                            WriteLog("INFO", $"Deleted old log file: {file}");
                        }
                        catch { /* Ignore individual file errors */ }
                    }
                }
            }
            catch { /* Ignore errors in log file cleanup */ }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Sets the log level from a string value
        /// </summary>
        public static void SetLogLevel(string level)
        {
            if (Enum.TryParse(level, true, out LogLevel parsedLevel))
            {
                _logLevel = parsedLevel;
            }
        }

        /// <summary>
        /// Gets the current log file path
        /// </summary>
        public static string GetLogFilePath() => _currentLogFilePath;

        /// <summary>
        /// Gets whether detailed metrics logging is enabled
        /// </summary>
        public static bool ShouldLogMetricsDetails() => _logMetricsCollectionDetails;
        #endregion

        #region Logging Methods
        /// <summary>
        /// Log a message at the specified level
        /// </summary>
        private static void Log(LogLevel level, string levelText, string message, Exception ex = null)
        {
            if (_logLevel <= level)
            {
                if (level == LogLevel.Warning && ShouldSuppressWarning(message))
                    return;
                
                if (ex != null)
                {
                    message = $"{message} - Exception: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        message += $" - Inner Exception: {ex.InnerException.Message}";
                    }
                }
                
                WriteLog(levelText, message);
            }
        }
        
        /// <summary>
        /// Logs a debug message
        /// </summary>
        public static void LogDebug(string message) => Log(LogLevel.Debug, "DEBUG", message);

        /// <summary>
        /// Logs an info message
        /// </summary>
        public static void LogInfo(string message) => Log(LogLevel.Info, "INFO", message);

        /// <summary>
        /// Logs a warning message
        /// </summary>
        public static void LogWarn(string message) => Log(LogLevel.Warning, "WARN", message);

        /// <summary>
        /// Logs an error message
        /// </summary>
        public static void LogError(string message, Exception ex = null) => Log(LogLevel.Error, "ERROR", message, ex);

        /// <summary>
        /// Writes a log message to file
        /// </summary>
        public static void WriteLog(string level, string message)
        {
            if (_isLogging) return; // Prevent recursive logging
            
            try
            {
                _isLogging = true;
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [{level}] - {message}{Environment.NewLine}";
                
                // Write to file
                File.AppendAllText(_currentLogFilePath, logMessage);
            }
            catch { /* Ignore logging errors */ }
            finally
            {
                _isLogging = false;
            }
        }
        #endregion

        #region Private Helper Methods
        /// <summary>
        /// Checks if a warning message should be suppressed based on settings
        /// </summary>
        private static bool ShouldSuppressWarning(string message)
        {
            if (!_suppressRepeatWarnings) return false;
            return _warningPatternRegex?.IsMatch(message) == true || _recentWarnings.Contains(message);
        }
        #endregion
    }
} 