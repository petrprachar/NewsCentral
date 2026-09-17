using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Publishes a verified display image to a protected, non-user-writable folder
/// (<see cref="DeliverySection.PublishedImagePath"/>) before it is referenced by
/// PersonalizationCSP — shared by both surfaces NewsService manages (lock screen, prefix
/// "lockscreen"; desktop wallpaper, prefix "wallpaper"), each in its own content-derived
/// namespace within the same folder. This closes two gaps in the cache-based apply it replaces:
///   1. The bytes on disk were never re-verified before the path was handed to the OS — the
///      image hash was checked once at download time against the signed index, never again at
///      apply time.
///   2. %ProgramData%\NewsCentral\ inherits C:\ProgramData's ACL, under which a standard user
///      can pre-create a team's images\generated\ folder and, as CREATOR OWNER, retain
///      delete-child rights over everything later written into it. Both surfaces are
///      machine-wide, pre-authentication-reachable (lock screen) or system-enforced (wallpaper)
///      surfaces, so that exposure matters for either.
/// File names are content-derived (<c>{prefix}-{hash16}{ext}</c>), so existence of the target
/// implies correctness and the CSP path changes exactly when the image content changes.
/// </summary>
public interface IImagePublisher
{
    /// <summary>
    /// Verifies <paramref name="sourcePath"/>'s SHA-256 against <paramref name="expectedHash"/>
    /// (accepts either the bare hex form or the "sha256:" prefixed form used in
    /// <c>ContentInfo.ImageHash</c>, case-insensitively), then copies it into the publish root
    /// under a content-derived name. Returns the absolute published path, or <c>null</c> on any
    /// failure — a missing source, a hash mismatch, or an I/O error. Callers must treat
    /// <c>null</c> as "no usable image," never as success: publishing unverified bytes is exactly
    /// what this method exists to prevent.
    /// </summary>
    string? Publish(string sourcePath, string expectedHash, string prefix);

    /// <summary>
    /// Deletes every published file matching <c>{prefix}-*</c> in the publish root except the one
    /// named by <paramref name="keepFileName"/> (or every matching one, if <c>null</c> — a legitimate
    /// steady state once a surface has nothing active to publish). Scoped strictly to
    /// <paramref name="prefix"/>, so sweeping one surface (e.g. "lockscreen") never touches another
    /// surface's files (e.g. "wallpaper-*"). Call this only at the START of a cycle, before anything
    /// is published that cycle — never in the same cycle a file was just written, since Windows may
    /// still hold it open from the apply that just ran.
    /// </summary>
    void SweepExcept(string? keepFileName, string prefix);
}

/// <inheritdoc cref="IImagePublisher"/>
public sealed class ImagePublisher(
    ServiceConfiguration config,
    ILogger<ImagePublisher> logger) : IImagePublisher
{
    private const string Sha256Prefix = "sha256:";

    public string? Publish(string sourcePath, string expectedHash, string prefix)
    {
        if (!File.Exists(sourcePath))
        {
            logger.LogWarning("Image publish skipped — source not found: {Path}", sourcePath);
            return null;
        }

        var publishRoot = config.Delivery.PublishedImagePath;
        if (string.IsNullOrWhiteSpace(publishRoot))
        {
            logger.LogError("Image publish failed — Delivery:PublishedImagePath is not configured");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not read source: {Path}", sourcePath);
            return null;
        }

        var actualHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expectedHex = NormalizeHash(expectedHash);

        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Image publish rejected — hash mismatch for {Path}: expected {Expected}, actual {Actual}",
                sourcePath, expectedHex, actualHex);
            return null;
        }

        var fileName = $"{prefix}-{actualHex[..16]}{Path.GetExtension(sourcePath)}";

        try
        {
            Directory.CreateDirectory(publishRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not create publish root: {Root}", publishRoot);
            return null;
        }

        var targetPath = Path.Combine(publishRoot, fileName);
        if (File.Exists(targetPath))
        {
            // The content-derived name makes existence equivalent to correctness — no re-copy.
            logger.LogDebug("Image already published: {Path}", targetPath);
            return targetPath;
        }

        var tempPath = Path.Combine(publishRoot, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, targetPath, overwrite: true);
            logger.LogDebug("Image published: {Path}", targetPath);
            return targetPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not write {Path}", targetPath);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            return null;
        }
    }

    public void SweepExcept(string? keepFileName, string prefix)
    {
        var publishRoot = config.Delivery.PublishedImagePath;
        if (string.IsNullOrWhiteSpace(publishRoot) || !Directory.Exists(publishRoot)) return;

        var glob = $"{prefix}-*";
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(publishRoot, glob).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Prefix} sweep: could not enumerate {Root}", prefix, publishRoot);
            return;
        }

        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            if (keepFileName is not null && string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(path);
                logger.LogDebug("{Prefix} sweep: deleted stale {File}", prefix, name);
            }
            catch (Exception ex)
            {
                // A locked stale file must never abort the sweep of its siblings.
                logger.LogWarning(ex, "{Prefix} sweep: failed to delete {File} — continuing", prefix, name);
            }
        }
    }

    private static string NormalizeHash(string hash) =>
        hash.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? hash[Sha256Prefix.Length..]
            : hash;

    /// <summary>
    /// Startup sanity check, called once from Program.cs after the host is built. Never throws and
    /// never stops the service: a bad or user-writable publish path just means every future
    /// <see cref="Publish"/> call fails (folder unusable) or the protection is void (folder
    /// writable) — every managed surface then falls back to its foreign-value-never-cleared /
    /// stays-frozen fail-closed behavior, which is the safe failure mode, but the operator needs
    /// to know why.
    /// </summary>
    internal static void CheckPublishFolderAcl(string publishRoot, ILogger logger)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(publishRoot) || !Path.IsPathRooted(publishRoot))
            {
                logger.LogError(
                    "Delivery:PublishedImagePath is empty or not an absolute path: '{Path}' — " +
                    "image publishing will fail every cycle for every surface", publishRoot);
                return;
            }
            fullPath = Path.GetFullPath(publishRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Delivery:PublishedImagePath is not a usable path: '{Path}' — " +
                "image publishing will fail every cycle for every surface", publishRoot);
            return;
        }

        try
        {
            if (!Directory.Exists(fullPath)) return;   // created on first publish; nothing to check yet

            var security = new DirectoryInfo(fullPath).GetAccessControl();
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            const FileSystemRights riskyRights =
                FileSystemRights.Write | FileSystemRights.Modify | FileSystemRights.FullControl;

            foreach (FileSystemAccessRule rule in
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & riskyRights) == 0) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid) continue;

                if (sid.Equals(usersSid) || sid.Equals(authUsersSid))
                {
                    var name = sid.Equals(usersSid) ? "Users (S-1-5-32-545)" : "Authenticated Users (S-1-5-11)";
                    logger.LogWarning(
                        "Delivery:PublishedImagePath ({Path}) grants Write/Modify/FullControl to {Identity} — " +
                        "this folder must not be user-writable or lock-screen publishing loses its protection",
                        fullPath, name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read ACL for {Path} — skipping the write-access sanity check", fullPath);
        }
    }
}
