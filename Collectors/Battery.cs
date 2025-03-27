using System;
using System.Management;
using Circul8Service.Collectors.Base;
using Circul8Service.Utils;

namespace Circul8Service.Collectors
{
    /// <summary>
    /// Collector for retrieving battery performance metrics from Windows systems.
    /// Handles battery presence detection, charge status, and capacity information.
    /// </summary>
    public class Battery : BaseCollector<Battery.BatteryPerformance>
    {
        #region Constants and Fields
        
        private const string ROOT_WMI = "ROOT\\WMI";
        private const string CIMV2_NAMESPACE = "root\\CIMV2";
        private static bool? _isBatteryPresent = null;
        
        #endregion
        
        #region WMI Query Properties
        
        /// <summary>
        /// WMI query for basic battery information from Win32_Battery class
        /// </summary>
        protected override string WmiQuery => "SELECT EstimatedChargeRemaining, BatteryStatus, DesignVoltage FROM Win32_Battery";
        
        /// <summary>
        /// WMI namespace for base battery information
        /// </summary>
        protected override string WmiNamespace => CIMV2_NAMESPACE;
        
        #endregion
        
        #region Battery Detection Methods
        
        /// <summary>
        /// Checks if a battery is present in the system.
        /// Results are cached to avoid repeated WMI queries.
        /// </summary>
        /// <returns>True if a battery is present, false otherwise</returns>
        public static bool IsBatteryPresent()
        {
            // Return cached result if we already checked
            if (_isBatteryPresent.HasValue)
                return _isBatteryPresent.Value;
                
            try
            {
                // Verify battery presence with a simple, reliable query
                using (var searcher = new ManagementObjectSearcher(CIMV2_NAMESPACE, "SELECT DeviceID FROM Win32_Battery"))
                using (var results = searcher.Get())
                {
                    // If there's at least one battery, return true
                    _isBatteryPresent = results.Count > 0;
                    
                    if (_isBatteryPresent.Value)
                        Logger.LogInfo("Battery detected in system");
                    else
                        Logger.LogInfo("No battery detected in system");
                        
                    return _isBatteryPresent.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error checking battery presence: {ex.Message}", ex);
                _isBatteryPresent = false;
                return false;
            }
        }
        
        #endregion

        #region Performance Metric Creation

        /// <summary>
        /// Creates a battery performance metric from a WMI object.
        /// Extracts basic info from Win32_Battery and then collects detailed information.
        /// </summary>
        /// <param name="obj">ManagementObject from WMI query</param>
        /// <returns>Populated BatteryPerformance object</returns>
        protected override BatteryPerformance CreatePerformanceMetric(ManagementObject obj)
        {
            var performance = new BatteryPerformance();
            
            try
            {
                ExtractBasicBatteryInfo(obj, performance);
                CollectDetailedBatteryInfo(performance);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error creating battery performance metric: {ex.Message}", ex);
            }
            
            return performance;
        }
        
        /// <summary>
        /// Extracts basic battery information from Win32_Battery class.
        /// Gets charge percentage, charging status, and design voltage.
        /// </summary>
        /// <param name="obj">ManagementObject from Win32_Battery</param>
        /// <param name="performance">Performance object to populate</param>
        private void ExtractBasicBatteryInfo(ManagementObject obj, BatteryPerformance performance)
        {
            // Get charge percentage
            performance.ChargePercentage = WmiHelper.ExtractDoubleValue(obj, "EstimatedChargeRemaining");
            
            // Get battery status
            try
            {
                int batteryStatus = WmiHelper.ExtractIntValue(obj, "BatteryStatus");
                performance.IsCharging = batteryStatus == 2;
                performance.IsDischarging = batteryStatus == 1;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error extracting BatteryStatus: {ex.Message}", ex);
                performance.IsCharging = false;
                performance.IsDischarging = false;
            }
            
            // Get design voltage
            try
            {
                performance.Voltage = WmiHelper.ExtractDoubleValue(obj, "DesignVoltage") / 1000.0;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error extracting DesignVoltage: {ex.Message}", ex);
                performance.Voltage = 0;
            }
        }

        #endregion
        
        #region Detailed Battery Information Collection
        
        /// <summary>
        /// Collects detailed battery information from ROOT\WMI namespace.
        /// Gets cycle count, capacity info, and detailed status.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectDetailedBatteryInfo(BatteryPerformance performance)
        {
            try
            {
                // Check if battery is present first
                if (!IsBatteryPresent())
                {
                    Logger.LogInfo("Skipping detailed battery info collection - no battery detected");
                    return;
                }
                
                // Collect each type of information separately with error handling for each
                CollectBatteryMetrics(performance);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error collecting detailed battery metrics: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Collects all detailed battery metrics with individual error handling.
        /// Separates each collection type to prevent a failure in one from affecting others.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectBatteryMetrics(BatteryPerformance performance)
        {
            try { CollectCycleCount(performance); } 
            catch (Exception ex) { Logger.LogError($"Error collecting cycle count: {ex.Message}", ex); }
            
            try { CollectCapacityInfo(performance); } 
            catch (Exception ex) { Logger.LogError($"Error collecting capacity info: {ex.Message}", ex); }
            
            try { CollectBatteryStatus(performance); } 
            catch (Exception ex) { Logger.LogError($"Error collecting battery status: {ex.Message}", ex); }
        }

        /// <summary>
        /// Collects battery cycle count information from BatteryCycleCount class.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectCycleCount(BatteryPerformance performance)
        {
            var results = WmiHelper.ExecuteWmiQuery("SELECT CycleCount FROM BatteryCycleCount", ROOT_WMI);
            if (results != null && results.Count > 0)
            {
                foreach (ManagementObject obj in results)
                {
                    try
                    {
                        using (obj)
                        {
                            performance.CycleCount = WmiHelper.ExtractIntValue(obj, "CycleCount");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error extracting cycle count from object: {ex.Message}", ex);
                    }
                }
            }
            else
            {
                Logger.LogInfo("BatteryCycleCount query returned no results");
            }
        }

        /// <summary>
        /// Collects battery capacity information from BatteryFullChargedCapacity class.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectCapacityInfo(BatteryPerformance performance)
        {
            var results = WmiHelper.ExecuteWmiQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", ROOT_WMI);
            if (results != null && results.Count > 0)
            {
                foreach (ManagementObject obj in results)
                {
                    try
                    {
                        using (obj)
                        {
                            performance.FullChargedCapacity = WmiHelper.ExtractLongValue(obj, "FullChargedCapacity");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error extracting battery capacity from object: {ex.Message}", ex);
                    }
                }
            }
            else
            {
                Logger.LogInfo("BatteryFullChargedCapacity query returned no results");
            }
        }

        /// <summary>
        /// Collects detailed battery status information from BatteryStatus class.
        /// Gets charge/discharge rates, remaining capacity, and current voltage.
        /// </summary>
        /// <param name="performance">Performance object to populate</param>
        private void CollectBatteryStatus(BatteryPerformance performance)
        {
            var query = "SELECT ChargeRate, DischargeRate, RemainingCapacity, Voltage, Charging, Discharging FROM BatteryStatus";
            var results = WmiHelper.ExecuteWmiQuery(query, ROOT_WMI);
                
            if (results != null && results.Count > 0)
            {
                foreach (ManagementObject obj in results)
                {
                    try
                    {
                        using (obj)
                        {
                            ExtractBatteryStatusDetails(obj, performance);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error extracting battery status details: {ex.Message}", ex);
                    }
                }
            }
            else
            {
                Logger.LogInfo("BatteryStatus query returned no results");
            }
        }
        
        /// <summary>
        /// Extracts detailed battery status properties from a BatteryStatus WMI object.
        /// </summary>
        /// <param name="obj">ManagementObject from BatteryStatus</param>
        /// <param name="performance">Performance object to populate</param>
        private void ExtractBatteryStatusDetails(ManagementObject obj, BatteryPerformance performance)
        {
            // Extract values safely with proper error handling
            performance.ChargeRate = WmiHelper.ExtractDoubleValue(obj, "ChargeRate");
            performance.DischargeRate = WmiHelper.ExtractDoubleValue(obj, "DischargeRate");
            performance.RemainingCapacity = WmiHelper.ExtractLongValue(obj, "RemainingCapacity");
            
            // Update voltage if available (more accurate than design voltage)
            double currentVoltage = WmiHelper.ExtractDoubleValue(obj, "Voltage") / 1000.0;
            if (currentVoltage > 0)
            {
                performance.Voltage = currentVoltage;
            }
            
            // Update charge status if available (more accurate than Win32_Battery)
            performance.IsCharging = obj["Charging"] != null ? Convert.ToBoolean(obj["Charging"]) : false;
            performance.IsDischarging = obj["Discharging"] != null ? Convert.ToBoolean(obj["Discharging"]) : false;
        }
        
        #endregion
        
        /// <summary>
        /// Represents battery performance metrics including charge status, capacity, and health
        /// </summary>
        public class BatteryPerformance : Base.BasePerformance
        {
            /// <summary>
            /// Number of charge/discharge cycles the battery has undergone
            /// </summary>
            public int CycleCount { get; set; }
            
            /// <summary>
            /// Capacity of the battery when fully charged (in mWh)
            /// </summary>
            public long FullChargedCapacity { get; set; }
            
            /// <summary>
            /// Current remaining capacity of the battery (in mWh)
            /// </summary>
            public long RemainingCapacity { get; set; }
            
            /// <summary>
            /// Current charge rate when charging (in mW)
            /// </summary>
            public double ChargeRate { get; set; }
            
            /// <summary>
            /// Current discharge rate when discharging (in mW)
            /// </summary>
            public double DischargeRate { get; set; }
            
            /// <summary>
            /// Battery voltage (in V)
            /// </summary>
            public double Voltage { get; set; }
            
            /// <summary>
            /// Current charge percentage (0-100%)
            /// </summary>
            public double ChargePercentage { get; set; }
            
            /// <summary>
            /// Whether the battery is currently charging
            /// </summary>
            public bool IsCharging { get; set; }
            
            /// <summary>
            /// Whether the battery is currently discharging
            /// </summary>
            public bool IsDischarging { get; set; }
            
            /// <summary>
            /// Estimated remaining time in hours until battery is fully charged or discharged
            /// </summary>
            public double EstimatedRemainingTime
            {
                get
                {
                    // Calculate time remaining based on charge/discharge rate
                    if (IsCharging && ChargeRate > 0)
                    {
                        // Calculate time to full charge
                        double remainingToCharge = FullChargedCapacity - RemainingCapacity;
                        return remainingToCharge / ChargeRate;
                    }
                    else if (IsDischarging && DischargeRate > 0)
                    {
                        // Calculate time to discharge
                        return RemainingCapacity / DischargeRate;
                    }
                    
                    return 0; // Not charging or discharging, or rate is zero
                }
            }
            
            /// <summary>
            /// Health percentage based on current full capacity vs design capacity
            /// </summary>
            public double HealthPercentage => (FullChargedCapacity > 0) ? (FullChargedCapacity / FullChargedCapacity * 100) : 0;
        }
        
    }
} 