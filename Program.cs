using System;
using System.Configuration;
using System.ServiceProcess;
using System.Configuration.Install;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Diagnostics;

namespace Circul8Service
{
    /// <summary>
    /// Main program class for the Circul8 service application.
    /// Handles command-line arguments for installation, uninstallation, and service execution.
    /// </summary>
    static class Program
    {
        private static bool _loggerInitialized = false;
        private const string ServiceName = "Circul8";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            try
            {
                InitializeLogger();

                if (args.Length > 0)
                {
                    switch (args[0].ToLower())
                    {
                        case "--install":
                        case "/install":
                        case "-i":
                            InstallService(args);
                            return;
                            
                        case "--uninstall":
                        case "/uninstall":
                        case "-u":
                            UninstallService();
                            return;
                            
                        case "--help":
                        case "/help":
                        case "-h":
                        case "/?":
                            ShowHelp();
                            return;
                    }
                }
                
                ServiceBase.Run(new Circul8Service());
            }
            catch (Exception ex)
            {
                LogError($"Unhandled exception in main: {ex.Message}");
                LogError(ex.InnerException?.Message ?? string.Empty);
            }
        }

        /// <summary>
        /// Initializes the logger with default settings
        /// </summary>
        private static void InitializeLogger()
        {
            if (!_loggerInitialized)
            {
                Utils.Logger.Initialize();
                _loggerInitialized = true;
            }
        }

        /// <summary>
        /// Logs an information message (avoids duplicate console output)
        /// </summary>
        private static void LogInfo(string message)
        {
            Console.WriteLine(message);
            Utils.Logger.LogInfo(message);
        }

        /// <summary>
        /// Logs an error message (avoids duplicate console output)
        /// </summary>
        private static void LogError(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(message);
                Console.ResetColor();
                Utils.Logger.LogError(message);
            }
        }

        /// <summary>
        /// Installs the service with the specified configuration parameters.
        /// </summary>
        /// <param name="args">Command line arguments containing configuration parameters.</param>
        private static void InstallService(string[] args)
        {
            try
            {
                InitializeLogger();
                LogInfo("Starting Circul8 service installation...");
                Utils.Logger.WriteLog("INFO", $"Command line arguments: {string.Join(" ", args)}");
                
                UpdateServiceConfig(args);
            }
            catch (Exception ex)
            {
                LogError($"Error installing service: {ex.Message}");
                Utils.Logger.WriteLog("ERROR", $"Stack trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    LogError($"Inner exception: {ex.InnerException.Message}");
                    Utils.Logger.WriteLog("ERROR", $"Inner exception stack trace: {ex.InnerException.StackTrace}");
                }
                
                throw;
            }
        }

        /// <summary>
        /// Uninstalls the service if it exists.
        /// </summary>
        private static void UninstallService()
        {
            try
            {
                LogInfo("Uninstalling Circul8 service...");
                
                if (!ServiceExists(ServiceName))
                {
                    LogInfo("Circul8 service is not installed.");
                    return;
                }

                StopService();
                UninstallServiceUsingSc();
                
                // Clean up temp directories
                CleanupTempDirectories();
                
                LogInfo("Circul8 service uninstalled successfully.");
            }
            catch (Exception ex)
            {
                LogError($"Error uninstalling service: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up Circul8 directories in temp and program data locations
        /// </summary>
        private static void CleanupTempDirectories()
        {
            try
            {
                // Clean up temp directory
                string tempDir = Path.Combine(Path.GetTempPath(), "Circul8");
                if (Directory.Exists(tempDir))
                {
                    LogInfo($"Cleaning up temp directory: {tempDir}");
                    Directory.Delete(tempDir, true);
                }

                // Clean up program data directory
                string programDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Circul8");
                if (Directory.Exists(programDataDir))
                {
                    LogInfo($"Cleaning up program data directory: {programDataDir}");
                    Directory.Delete(programDataDir, true);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error cleaning up directories: {ex.Message}");
            }
        }

        private static void StopService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        LogInfo("Stopping running service...");
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        LogInfo("Service stopped.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error stopping service: {ex.Message}");
            }
        }

        private static void UninstallServiceUsingSc()
        {
            var uninstallStartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"delete {ServiceName}",
                CreateNoWindow = false,
                UseShellExecute = true,
                Verb = "runas"
            };
            
            using (var process = Process.Start(uninstallStartInfo))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new Exception($"SC delete failed with exit code: {process.ExitCode}");
                }
            }
        }

        /// <summary>
        /// Updates the service configuration based on command line arguments.
        /// </summary>
        /// <param name="args">Command line arguments containing configuration parameters.</param>
        private static void UpdateServiceConfig(string[] args)
        {
            try
            {
                string logDirectory = GetLogDirectory();
                string logFile = Path.Combine(logDirectory, "service_config_log.txt");
                
                Utils.Logger.WriteLog("INFO", "Starting service configuration update");
                Utils.Logger.WriteLog("INFO", $"Command line arguments: {string.Join(" ", args)}");
                
                ProcessCommandLineArguments(args, logFile);
                UninstallExistingService(logFile);
                InstallServiceUsingSc(args, logFile, logDirectory);
            }
            catch (Exception ex)
            {
                LogError($"Error updating service configuration: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Processes command line arguments and logs them.
        /// </summary>
        /// <param name="args">Command line arguments to process.</param>
        /// <param name="logFile">Path to the log file.</param>
        private static void ProcessCommandLineArguments(string[] args, string logFile)
        {
            if (args == null || args.Length < 2)
            {
                throw new ArgumentException("Invalid command line arguments");
            }

            LogToFile(logFile, $"Processing command line arguments: {string.Join(" ", args)}");
            
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("--collector") || arg.StartsWith("--frequency") || 
                    arg.StartsWith("--average") || arg.StartsWith("--output"))
                {
                    LogToFile(logFile, $"Processing argument: {arg}");
                    ProcessConfigurationArgument(arg, logFile);
                }
            }
        }

        private static void ProcessConfigurationArgument(string arg, string logFile)
        {
            try
            {
                string[] parts = arg.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    LogToFile(logFile, $"Invalid argument format: {arg}");
                    return;
                }

                string key = parts[0].TrimStart('-');
                string value = parts[1].Trim('"');
                
                Configuration config = ConfigurationManager.OpenExeConfiguration(Assembly.GetExecutingAssembly().Location);
                
                if (config.AppSettings.Settings[key] != null)
                {
                    config.AppSettings.Settings[key].Value = value;
                }
                else
                {
                    config.AppSettings.Settings.Add(key, value);
                }
                
                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
                
                LogToFile(logFile, $"Updated configuration: {key}={value}");
            }
            catch (Exception ex)
            {
                LogToFile(logFile, $"Error processing configuration argument: {arg}, Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Uninstalls the service if it already exists.
        /// </summary>
        /// <param name="logFile">Path to the log file.</param>
        private static void UninstallExistingService(string logFile)
        {
            if (ServiceExists(ServiceName))
            {
                LogToFile(logFile, "Service already exists, uninstalling it first");
                LogInfo("Service already exists, uninstalling it first...");
                
                try
                {
                    // Stop the service if it's running
                    using (ServiceController sc = new ServiceController(ServiceName))
                    {
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            LogToFile(logFile, "Stopping running service");
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                            LogToFile(logFile, "Service stopped");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToFile(logFile, $"Error stopping service: {ex.Message}");
                }
                
                // Uninstall the service using sc.exe
                try
                {
                    LogToFile(logFile, "Uninstalling service using sc.exe");
                    
                    ProcessStartInfo uninstallStartInfo = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"delete {ServiceName}",
                        CreateNoWindow = false,
                        UseShellExecute = true,
                        Verb = "runas" // Run as administrator
                    };
                    
                    using (Process process = Process.Start(uninstallStartInfo))
                    {
                        process.WaitForExit();
                        LogToFile(logFile, $"SC delete process exited with code: {process.ExitCode}");
                    }
                    
                    LogToFile(logFile, "Service uninstalled successfully");
                }
                catch (Exception ex)
                {
                    LogToFile(logFile, $"Error uninstalling service: {ex.Message}");
                    // Continue with installation anyway
                }
            }
        }

        /// <summary>
        /// Installs the service using sc.exe command.
        /// </summary>
        /// <param name="args">Command line arguments containing configuration parameters.</param>
        /// <param name="logFile">Path to the log file.</param>
        /// <param name="logDirectory">Directory for log files.</param>
        private static void InstallServiceUsingSc(string[] args, string logFile, string logDirectory)
        {
            LogToFile(logFile, "Installing service using sc.exe");
            
            string exePath = Assembly.GetExecutingAssembly().Location;
            LogToFile(logFile, $"Executable path: {exePath}");
            
            // Build the command line arguments to pass to the service
            string serviceParams = string.Join(" ", args.Skip(1));
            string binPathValue = $"\"{exePath}\" {serviceParams}";
            LogToFile(logFile, $"Service binary path with parameters: {binPathValue}");
            
            // Create the service using sc.exe
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"create {ServiceName} binPath= \"{binPathValue}\" start= auto DisplayName= \"Circul8 System Monitoring Service\" depend= Winmgmt",
                CreateNoWindow = false,
                UseShellExecute = true,
                Verb = "runas" // Run as administrator
            };
            
            LogInfo("Installing service using sc.exe...");
            LogToFile(logFile, $"SC command: {startInfo.FileName} {startInfo.Arguments}");
            
            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                LogToFile(logFile, $"SC create process exited with code: {process.ExitCode}");
                
                if (process.ExitCode != 0)
                {
                    string errorMessage = $"Failed to install service. SC exit code: {process.ExitCode}";
                    LogToFile(logFile, errorMessage);
                    throw new Exception(errorMessage);
                }
            }
            
            // Set service description
            startInfo.Arguments = $"description {ServiceName} \"Collects system metrics\"";
            LogToFile(logFile, $"SC description command: {startInfo.FileName} {startInfo.Arguments}");
            
            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                LogToFile(logFile, $"SC description process exited with code: {process.ExitCode}");
            }
            
            // Configure service recovery options
            startInfo.Arguments = $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/120000/restart/300000";
            LogToFile(logFile, $"SC failure command: {startInfo.FileName} {startInfo.Arguments}");
            
            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                LogToFile(logFile, $"SC failure process exited with code: {process.ExitCode}");
            }
            
            LogToFile(logFile, "Service installation completed");
            LogInfo("Circul8 service installed successfully with the specified configuration.");
            LogInfo($"Installation logs saved to: {logDirectory}");
        }
        
        /// <summary>
        /// Checks if a service exists.
        /// </summary>
        /// <param name="serviceName">Name of the service to check.</param>
        /// <returns>True if the service exists, false otherwise.</returns>
        private static bool ServiceExists(string serviceName)
        {
            return ServiceController.GetServices().Any(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Logs a message to a file, creating the directory if needed.
        /// Ensures proper file handling with using statements.
        /// </summary>
        /// <param name="filePath">Path to the log file.</param>
        /// <param name="message">Message to log.</param>
        private static void LogToFile(string filePath, string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string logMessage = $"[{timestamp}] {message}";
                
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.AppendAllText(filePath, logMessage + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows command-line help.
        /// </summary>
        private static void ShowHelp()
        {
            Console.WriteLine("Circul8 Service - System Monitoring Service");
            Console.WriteLine("Usage: Circul8Service.exe [command] [options]");
            Console.WriteLine("\nCommands:");
            Console.WriteLine("  --install, -i     Install the service");
            Console.WriteLine("  --uninstall, -u   Uninstall the service");
            Console.WriteLine("  --help, -h        Show this help message");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --collector enabled=\"[collectors]\"  Enable specific collectors (comma-separated)");
            Console.WriteLine("                                    Available: Disk,Battery,Memory,Eventlogs,");
            Console.WriteLine("                                              Processor,SystemInfo");
            Console.WriteLine("  --frequency [minutes]              Set collection frequency in minutes");
            Console.WriteLine("  --average after=[count]           Calculate averages after N samples");
            Console.WriteLine("\nExample:");
            Console.WriteLine("  Circul8Service.exe --install --collector enabled=\"Disk,Memory,Processor\"");
            Console.WriteLine("                     --frequency=5 --average after=3");
        }

        /// <summary>
        /// Gets the log directory path for installation logs (always in temp).
        /// </summary>
        /// <returns>Path to the log directory in temp folder.</returns>
        private static string GetLogDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string logDir = Path.Combine(baseDir, "logs");
            
            try
            {
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating log directory: {ex.Message}");
                logDir = baseDir;
            }
            
            return logDir;
        }
    }
}
