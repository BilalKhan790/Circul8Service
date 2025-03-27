using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Circul8Service.Utils
{
    /// <summary>
    /// Helper class for security-related functionality, including certificate validation.
    /// </summary>
    public static class SecurityHelper
    {
        /// <summary>
        /// Creates a certificate validation callback based on the provided configuration value.
        /// </summary>
        /// <param name="type">
        ///   "true" = Use default .NET certificate validation
        ///   "false" = Skip certificate validation (not secure for production)
        ///   Path to certificate file = Validate against specific certificate
        /// </param>
        /// <returns>A certificate validation callback or null for default behavior</returns>
        public static RemoteCertificateValidationCallback CertificateValidationCallback(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return null; // Use default .NET behavior
            }

            switch (type.ToLowerInvariant())
            {
                // Do not change default .NET behavior when given True
                case "true":
                    return null;

                // Do not verify certificate when given False
                case "false":
                    Logger.LogWarn("Certificate verification is disabled. This is not secure for production environments.");
                    return (sender, certificate, chain, errors) => true;

                // Assume it points to a file path of a certificate that we will check against
                default:
                    try
                    {
                        var cert = new X509Certificate2(type);
                        Logger.LogInfo($"Using custom certificate for validation: {cert.Subject}, Thumbprint: {cert.Thumbprint}");
                        return (sender, certificate, chain, errors) =>
                            errors == SslPolicyErrors.None ||
                            string.Equals(cert.Thumbprint, certificate.GetCertHashString(), StringComparison.InvariantCultureIgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to load certificate from {type}: {ex.Message}");
                        return null; // Fall back to default behavior
                    }
            }
        }

        /// <summary>
        /// Installs the certificate validation callback at the ServicePointManager level,
        /// affecting all HTTPS connections made by the application.
        /// </summary>
        /// <param name="type">The certificate verification configuration</param>
        public static void InstallCertificateVerification(string type)
        {
            try
            {
                ServicePointManager.ServerCertificateValidationCallback = CertificateValidationCallback(type);
                Logger.LogInfo($"Certificate verification configured: {(type ?? "default")}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to configure certificate verification: {ex.Message}");
            }
        }
    }
} 