using System;
using System.Collections.Generic;
using System.Management;

namespace Circul8Service.Collectors.Base
{
    /// <summary>
    /// Base collector for all performance metrics that use WMI queries.
    /// Provides common functionality for executing WMI queries and error handling.
    /// </summary>
    /// <typeparam name="T">The performance metric type</typeparam>
    public abstract class BaseCollector<T> : IInfoCollector<T> where T : class
    {
        /// <summary>
        /// The WMI query string to execute for collecting data
        /// </summary>
        protected abstract string WmiQuery { get; }
        
        /// <summary>
        /// The WMI namespace to query (defaults to root\CIMV2)
        /// </summary>
        protected virtual string WmiNamespace => "root\\CIMV2";
        
        /// <summary>
        /// Abstract method that converts a WMI ManagementObject to a performance metric
        /// </summary>
        /// <param name="wmiObject">The WMI object containing the raw data</param>
        /// <returns>A populated performance metric</returns>
        protected abstract T CreatePerformanceMetric(ManagementObject wmiObject);

        /// <summary>
        /// Retrieves a list of performance metrics by executing the WMI query
        /// </summary>
        /// <returns>List of performance metrics, or empty list if query fails</returns>
        public virtual List<T> GetInfo()
        {
            var metrics = new List<T>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(WmiNamespace, WmiQuery))
                {
                    try
                    {
                        using (var results = searcher.Get())
                        {
                            if (results.Count > 0)
                            {
                                foreach (ManagementObject obj in results)
                                {
                                    try
                                    {
                                        using (obj)
                                        {
                                            metrics.Add(CreatePerformanceMetric(obj));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Utils.Logger.LogError($"Error processing WMI object: {ex.Message}", ex);
                                    }
                                }
                            }
                            else
                            {
                                Utils.Logger.LogInfo($"WMI query returned no results: {WmiQuery} in namespace {WmiNamespace}");
                            }
                        }
                    }
                    catch (ManagementException mex)
                    {
                        Utils.Logger.LogError($"WMI query execution error: {mex.Message}. Invalid query: '{WmiQuery}' in namespace '{WmiNamespace}'", mex);
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError($"Error in collector GetInfo: {ex.Message}", ex);
            }
            return metrics;
        }

        /// <summary>
        /// Gets a single performance metric instance.
        /// This is the primary method used by the service to collect metrics.
        /// </summary>
        /// <returns>
        /// The first performance metric from GetInfo, or null if no metrics were found.
        /// Returning null prevents empty metrics from being saved or sent to cloud.
        /// </returns>
        public virtual T GetMetrics()
        {
            var list = GetInfo();
            
            // If WMI query failed or returned no results, return null instead of empty metrics
            // This will prevent empty/zero metrics from being saved and sent to cloud
            if (list.Count == 0)
            {
                return null;
            }
            
            return list[0];
        }
    }
} 