using System;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace NewsCentral.Services
{
    public class WindowsIdentityService
    {
        private readonly IConfiguration _configuration;

        public WindowsIdentityService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string? GetCurrentUserUPN()
        {
            var useMock = _configuration.GetValue<bool>("Authentication:UseMockUPN");

            if (useMock)
            {
                var mockUPN = _configuration.GetValue<string>("Authentication:MockUPN");
                System.Diagnostics.Debug.WriteLine($"Using mock UPN: {mockUPN}");
                return mockUPN;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "whoami",
                    Arguments = "/upn",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to start whoami process");
                    return null;
                }

                var upn = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();

                process.WaitForExit();

                if (process.ExitCode != 0 || string.IsNullOrEmpty(upn))
                {
                    System.Diagnostics.Debug.WriteLine($"whoami failed: {error}");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"Detected UPN: {upn}");
                return upn;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting UPN: {ex.Message}");
                return null;
            }
        }

        public bool IsAutoLoginEnabled()
        {
            return _configuration.GetValue<bool>("Authentication:EnableAutoLogin");
        }
    }
}
