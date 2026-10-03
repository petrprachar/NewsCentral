#pragma warning disable CA1416 // Validate platform compatibility — Windows-only P/Invoke; this
                               // whole file is Windows-only in practice, matching MauiProgram.cs.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;

namespace NewsCentral.Services
{
    public partial class WindowsIdentityService
    {
        private readonly IConfiguration _configuration;

        public WindowsIdentityService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private const int NameUserPrincipal = 8;   // EXTENDED_NAME_FORMAT.NameUserPrincipal
        private const int ErrorMoreData = 234;      // ERROR_MORE_DATA

        // Source-generated, NativeAOT-compatible P/Invoke — no StringBuilder marshalling (unsupported
        // by LibraryImport), no Process, no PATH-resolved external executable. nSize is PULONG
        // (an in/out pointer to a ULONG) — passed as IntPtr rather than `ref uint` because a
        // by-ref blittable parameter makes the LibraryImport source generator emit pointer code
        // that requires <AllowUnsafeBlocks>, which this project does not set.
        [LibraryImport("secur32.dll", SetLastError = true)]
        private static partial int GetUserNameExW(int nameFormat, IntPtr lpNameBuffer, IntPtr nSize);

        [SupportedOSPlatform("windows")]
        private static string? GetUserPrincipalNameNative()
        {
            var sizePtr = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(sizePtr, 0);

                // Step 1 — probe for the required buffer size. The API cannot succeed against a
                // zero-length buffer for a non-empty name, so a nonzero return here is unexpected;
                // treat it the same as "no name available" rather than trusting an empty result.
                if (GetUserNameExW(NameUserPrincipal, IntPtr.Zero, sizePtr) != 0)
                    return null;

                int error = Marshal.GetLastWin32Error();
                uint size = unchecked((uint)Marshal.ReadInt32(sizePtr));
                if (error != ErrorMoreData || size == 0)
                {
                    // ERROR_NONE_MAPPED (1332) on a non-domain account is the normal "no UPN"
                    // case, not a real error — logged at Debug either way so a genuine failure
                    // stays visible.
                    System.Diagnostics.Debug.WriteLine($"GetUserNameExW: no UPN available (Win32 error {error})");
                    return null;
                }

                // Step 2 — allocate the requested buffer (characters, including the null
                // terminator) and retry with it.
                var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
                try
                {
                    if (GetUserNameExW(NameUserPrincipal, buffer, sizePtr) == 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"GetUserNameExW failed on retry (Win32 error {Marshal.GetLastWin32Error()})");
                        return null;
                    }

                    var upn = Marshal.PtrToStringUni(buffer);
                    return string.IsNullOrEmpty(upn) ? null : upn;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sizePtr);
            }
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

            return GetUserPrincipalNameNative();
        }

        public bool IsAutoLoginEnabled()
        {
            return _configuration.GetValue<bool>("Authentication:EnableAutoLogin");
        }
    }
}

#pragma warning restore CA1416
