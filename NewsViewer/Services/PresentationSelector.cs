using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;

namespace NewsViewer.Services;

public sealed class PresentationSelector
{
    private readonly string _cacheRootPath;
    private readonly EcdsaSignatureService _ecdsa;
    private readonly IConfiguration _configuration;
    private readonly bool _bypassImageIntegrityCheck;

    public PresentationSelector(
        string cacheRootPath,
        EcdsaSignatureService ecdsa,
        IConfiguration configuration,
        bool bypassImageIntegrityCheck = false)
    {
        _cacheRootPath             = cacheRootPath;
        _ecdsa                     = ecdsa;
        _configuration             = configuration;
        _bypassImageIntegrityCheck = bypassImageIntegrityCheck;
    }

    /// <summary>
    /// Reads index.json for each configured team, filters to assignments that are
    /// active right now, and returns the one with the most recent PresentationLastModified.
    /// Returns null if no qualifying assignment is found or all indexes fail verification.
    /// </summary>
    public (PublishedAssignmentIndex? Assignment, string? ImagePath) SelectActive(string[] teams)
    {
        var now = DateTime.Now;
        var todayKey = now.DayOfWeek == DayOfWeek.Sunday
            ? "7"
            : ((int)now.DayOfWeek).ToString();

        PublishedAssignmentIndex? best = null;

        foreach (var team in teams)
        {
            var indexPath = Path.Combine(_cacheRootPath, team, "index.json");
            if (!File.Exists(indexPath)) continue;

            TeamIndexFile? index;
            try
            {
                var json = File.ReadAllText(indexPath);
                index = JsonSerializer.Deserialize<TeamIndexFile>(json, JsonDefaults.Options);
            }
            catch { continue; }

            if (index?.PublishedAssignments is null) continue;

            var keys          = SigningKeyConfigurationReader.GetPublicKeys(_configuration, team);
            var result        = _ecdsa.Verify(index, keys);
            var requireSigned = _configuration.GetValue<bool>("Signing:RequireSignedIndex");
            if (SignatureGate.ShouldReject(result, requireSigned, out var reason))
            {
                System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index rejected — {reason}");
                continue;
            }
            System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index accepted — {reason}");

            foreach (var a in index.PublishedAssignments)
            {
                if (a.ScheduleStart > now || a.ScheduleEnd < now) continue;
                if (!a.DaysOfWeek.Split(',').Contains(todayKey)) continue;

                if (best is null || a.PresentationLastModified > best.PresentationLastModified)
                    best = a;
            }
        }

        if (best is null) return (null, null);

        var imagePath = Path.Combine(
            _cacheRootPath,
            best.Content.ImagePath.Replace('/', Path.DirectorySeparatorChar));

        if (!_bypassImageIntegrityCheck && !VerifyImageHash(imagePath, best.Content.ImageHash))
            return (best, null);

        return (best, imagePath);
    }

    private static bool VerifyImageHash(string imagePath, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: no hash in index — skipping integrity check");
            return true;
        }

        if (!File.Exists(imagePath))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: file not found");
            return false;
        }

        try
        {
            string prefix   = "sha256:";
            string expected = storedHash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? storedHash[prefix.Length..]
                : storedHash;

            using var sha256    = SHA256.Create();
            using var stream    = File.OpenRead(imagePath);
            byte[] hashBytes    = sha256.ComputeHash(stream);
            string actual       = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ImageHash] {Path.GetFileName(imagePath)}: hash mismatch — image rejected");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: hash OK");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: error during verification — {ex.Message}");
            return false;
        }
    }
}
