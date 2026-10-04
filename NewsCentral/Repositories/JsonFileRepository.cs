using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Services;

namespace NewsCentral.Repositories;

/// <summary>
/// General-purpose JSON file repository for non-team-scoped data.
/// Used for: config/users.json, config/teams.json, and similar global files.
///
/// Path model:
///   relativeBasePath = "config"   (relative to IStorageService root = DataPath)
///   entityFolder     = ""         (appended to relativeBasePath; empty = files sit directly in base)
///   file             = "{id}.json"
///
///   → resolved path: "config/users.json"
///
/// Unlike TeamAwareRepository, files here have no prefix — the id IS the filename.
/// e.g. GetById("users") → "config/users.json"
/// </summary>
public class JsonFileRepository<T> : IRepository<T> where T : class, IEntity
{
    private readonly IStorageService _storage;
    private readonly string _relativeBasePath;
    private readonly string _entityFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Primary constructor — inject IStorageService from DI or pass directly.
    /// Use this in all new and migrated code.
    ///
    /// Example (UserService after migration):
    ///   new JsonFileRepository&lt;UsersCollection&gt;(_storage, "config", "")
    ///   → reads/writes  DataPath/config/users.json
    /// </summary>
    public JsonFileRepository(
        IStorageService storage,
        string relativeBasePath,
        string entityFolder)
    {
        _storage = storage;
        _relativeBasePath = relativeBasePath;
        _entityFolder = entityFolder;
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    // e.g. "config" when relativeBasePath="config", entityFolder=""
    // e.g. "config/teams" when relativeBasePath="config", entityFolder="teams"
    private string RelativeFolderPath
    {
        get
        {
            var parts = new[] { _relativeBasePath, _entityFolder }
                .Where(p => !string.IsNullOrEmpty(p));
            return string.Join("/", parts); // "" when both are empty
        }
    }

    // e.g. "config/users.json"  (id = "users")
    private string RelativeFilePath(string id)
    {
        var folder = RelativeFolderPath;
        var file   = $"{id}.json";
        return string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";
    }

    // ── Shared serializer options ─────────────────────────────────────────────

    // JsonStringEnumConverter added to all methods for consistency —
    // previously CreateAsync/UpdateAsync included it but GetAllAsync/GetByIdAsync did not.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ── IRepository<T> ───────────────────────────────────────────────────────

    public async Task<List<T>> GetAllAsync()
    {
        var files = await _storage.ListFilesAsync(RelativeFolderPath, "*.json");
        var entities = new List<T>();

        foreach (var relativePath in files)
        {
            try
            {
                var json = await _storage.ReadTextAsync(relativePath);
                if (json == null) continue;

                var entity = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (entity != null)
                    entities.Add(entity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {relativePath}: {ex.Message}");
            }
        }

        return entities;
    }

    public async Task<T?> GetByIdAsync(string id)
    {
        var json = await _storage.ReadTextAsync(RelativeFilePath(id));
        if (json == null) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deserializing {id}: {ex.Message}");
            return null;
        }
    }

    public async Task<T> CreateAsync(T entity)
    {
        await _lock.WaitAsync();
        try
        {
            var path = RelativeFilePath(entity.GetId());

            if (await _storage.FileExistsAsync(path))
                throw new InvalidOperationException(
                    $"Entity {entity.GetId()} already exists at {path}");

            await _storage.WriteTextAsync(
                path,
                JsonSerializer.Serialize(entity, JsonOptions));

            return entity;
        }
        finally { _lock.Release(); }
    }

    public async Task<T> UpdateAsync(T entity)
    {
        await _lock.WaitAsync();
        try
        {
            var path = RelativeFilePath(entity.GetId());

            if (!await _storage.FileExistsAsync(path))
                throw new InvalidOperationException(
                    $"Entity {entity.GetId()} does not exist at {path}");

            await _storage.WriteTextAsync(
                path,
                JsonSerializer.Serialize(entity, JsonOptions));

            return entity;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var path = RelativeFilePath(id);

            if (!await _storage.FileExistsAsync(path))
                return false;

            await _storage.DeleteFileAsync(path);
            return true;
        }
        finally { _lock.Release(); }
    }

    public Task<bool> ExistsAsync(string id) =>
        _storage.FileExistsAsync(RelativeFilePath(id));
}
