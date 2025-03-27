using System.Collections.Generic;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Interface for all metric collectors in the Circul8 monitoring system.
    /// Defines the contract for classes that gather system performance information.
    /// </summary>
    /// <typeparam name="T">The type of performance data collected (must be a class)</typeparam>
    public interface IInfoCollector<T> where T : class
    {
        /// <summary>
        /// Gets a list of performance data objects from the system.
        /// This method typically performs the actual data collection from the source.
        /// </summary>
        /// <returns>A list of performance data objects, or empty list if collection fails</returns>
        List<T> GetInfo();
        
        /// <summary>
        /// Gets a single performance data object representing the current system state.
        /// This is the primary method called by the service for regular metric collection.
        /// </summary>
        /// <returns>
        /// A performance data object with the latest metrics, or null if collection fails.
        /// Null values indicate collection failure and will be filtered out by the service.
        /// </returns>
        T GetMetrics();
    }
} 