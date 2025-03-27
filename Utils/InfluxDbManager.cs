using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading.Tasks;
using Circul8Service.Utils;

namespace Circul8Service.Utils
{
    public class InfluxDbManager : IDisposable
    {
        private readonly InfluxDBClient _client;
        private readonly string _bucket;
        private readonly string _org;
        private bool _disposed;

        public InfluxDbManager()
        {
            // Configure certificate verification first
            string certificateVerification = ConfigurationManager.AppSettings["CertificateVerification"] ?? "true";
            SecurityHelper.InstallCertificateVerification(certificateVerification);
            
            // Get InfluxDB v2 settings from config
            string url = ConfigurationManager.AppSettings["InfluxDbUrl"] ?? "https://localhost:8086";
            string token = ConfigurationManager.AppSettings["InfluxDbToken"];
            _org = ConfigurationManager.AppSettings["InfluxDbOrg"] ?? "Circul8";
            _bucket = ConfigurationManager.AppSettings["InfluxDbBucket"] ?? "Circul8";

            if (string.IsNullOrEmpty(token))
            {
                throw new ConfigurationErrorsException("InfluxDB token is required but not configured");
            }

            // Create InfluxDB v2 client
            _client = InfluxDBClientFactory.Create(url, token.ToCharArray());
        }

        public async Task<bool> WriteMetricsAsync(List<object> metrics)
        {
            try
            {
                if (metrics == null || metrics.Count == 0)
                    return false;

                var writeApi = _client.GetWriteApiAsync();
                var points = new List<PointData>();

                foreach (var metric in metrics)
                {
                    var point = ConvertToPointData(metric);
                    if (point != null)
                    {
                        points.Add(point);
                    }
                }

                if (points.Count > 0)
                {
                    await writeApi.WritePointsAsync(_bucket, _org, points);
                    Logger.LogDebug($"Successfully wrote {points.Count} points to InfluxDB");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error writing metrics to InfluxDB: {ex.Message}");
                return false;
            }
        }

        private PointData ConvertToPointData(object metric)
        {
            try
            {
                var measurement = GetPropertyValue(metric, "measurement")?.ToString();
                var tags = GetPropertyValue(metric, "tags") as dynamic;
                var fields = GetPropertyValue(metric, "fields") as dynamic;
                var timestamp = GetPropertyValue(metric, "timestamp") as long?;

                if (string.IsNullOrEmpty(measurement) || fields == null)
                    return null;

                var point = PointData.Measurement(measurement);

                // Add tags
                if (tags != null)
                {
                    foreach (var tag in GetProperties(tags))
                    {
                        point = point.Tag(tag.Key, tag.Value?.ToString());
                    }
                }

                // Add fields
                foreach (var field in GetProperties(fields))
                {
                    point = point.Field(field.Key, field.Value);
                }

                // Add timestamp if available
                if (timestamp.HasValue)
                {
                    point = point.Timestamp(timestamp.Value, WritePrecision.Ms);
                }

                return point;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error converting metric to PointData: {ex.Message}");
                return null;
            }
        }

        private object GetPropertyValue(object obj, string propertyName)
        {
            try
            {
                return obj.GetType().GetProperty(propertyName)?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, object> GetProperties(dynamic obj)
        {
            var properties = new Dictionary<string, object>();
            try
            {
                foreach (var prop in obj.GetType().GetProperties())
                {
                    properties[prop.Name] = prop.GetValue(obj);
                }
            }
            catch
            {
                // Ignore errors when getting properties
            }
            return properties;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _client?.Dispose();
                _disposed = true;
            }
        }
    }
} 