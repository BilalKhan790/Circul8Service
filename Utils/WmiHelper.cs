using System;
using System.Management;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Helper for WMI queries and data extraction with built-in error handling.
    /// </summary>
    public static class WmiHelper
    {
        // Common WMI namespaces
        public const string WMI_NAMESPACE_CIMV2 = "root\\CIMV2";

        /// <summary>
        /// Executes a WMI query and returns the results.
        /// Returns null if query fails.
        /// </summary>
        /// <param name="queryString">The WMI query string</param>
        /// <param name="namespacePath">The WMI namespace path</param>
        /// <returns>A collection of management objects or null if the query fails</returns>
        public static ManagementObjectCollection ExecuteWmiQuery(string queryString, string namespacePath)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(namespacePath, queryString))
                {
                    try
                    {
                        var results = searcher.Get();
                        if (results.Count == 0)
                        {
                            Logger.LogInfo($"WMI query returned no results: '{queryString}' in namespace '{namespacePath}'");
                        }
                        return results;
                    }
                    catch (ManagementException mex)
                    {
                        Logger.LogError($"WMI query execution failed: '{queryString}' in namespace '{namespacePath}'. Error: {mex.Message}", mex);
                        if (mex.Message.Contains("Invalid query") || mex.Message.Contains("Invalid class"))
                        {
                            Logger.LogError($"Query syntax error detected. Please check the query: '{queryString}'");
                        }
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to execute WMI query in {namespacePath}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Extracts an integer value from a management object.
        /// Returns 0 if property is missing or conversion fails.
        /// </summary>
        public static int ExtractIntValue(ManagementObject obj, string propertyName)
        {
            try
            {
                if (obj[propertyName] != null)
                {
                    return Convert.ToInt32(obj[propertyName]);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error converting {propertyName} to integer: {ex.Message}", ex);
            }
            return 0;
        }

        /// <summary>
        /// Extracts a double value from a management object.
        /// Returns 0.0 if property is missing or conversion fails.
        /// </summary>
        public static double ExtractDoubleValue(ManagementObject obj, string propertyName)
        {
            try
            {
                if (obj[propertyName] != null)
                {
                    return Convert.ToDouble(obj[propertyName]);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error converting {propertyName} to double: {ex.Message}", ex);
            }
            return 0.0;
        }

        /// <summary>
        /// Extracts a long value from a management object.
        /// Returns 0 if property is missing or conversion fails.
        /// </summary>
        public static long ExtractLongValue(ManagementObject obj, string propertyName)
        {
            try
            {
                if (obj[propertyName] != null)
                {
                    return Convert.ToInt64(obj[propertyName]);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error converting {propertyName} to long: {ex.Message}", ex);
            }
            return 0;
        }

        /// <summary>
        /// Extracts a string value from a management object.
        /// Returns empty string if property is missing or conversion fails.
        /// </summary>
        public static string ExtractStringValue(ManagementObject obj, string propertyName)
        {
            try
            {
                if (obj[propertyName] != null)
                {
                    return obj[propertyName].ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error converting {propertyName} to string: {ex.Message}", ex);
            }
            return string.Empty;
        }
    }
} 