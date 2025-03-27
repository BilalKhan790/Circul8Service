using System;

namespace Circul8Service.Collectors.Base
{
    /// <summary>
    /// Base class for all performance metrics models.
    /// Provides common properties shared by all metric collectors.
    /// </summary>
    public abstract class BasePerformance
    {
        /// <summary>
        /// UTC timestamp when metrics were collected.
        /// Automatically set to current time when instance is created.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    }
}