using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Services;

namespace NewsCentral.Repositories;

public class TeamAwareRepository<T> : IRepository<T> where T : class, IEntity
{
    private readonly IStorageService _storage;
    private readonly string _teamFolderName;
    private readonly string _entityFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Primary constructor — inject IStorageService from DI or pass directly.
    /// Use this in all new and migrated code.
    /// </summary>
    public TeamAwareRepository(
        IStorageService storage,
        string teamFolderName,
        string entityFolder)
    {
        _storage = storage;
        _teamFolderName = teamFolderName;
        _entityFolder = entityFolder;
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    // e.g. "team-alpha/content/presentations"
    private string RelativeFolderPath =>
        $"{_teamFolderName}/content/{_entityFolder}";

    // e.g. "team-alpha/content/presentations/pres_abc123.json"
    private string RelativeFilePath(string id) =>
        $"{RelativeFolderPath}/{FilePrefix}_{id}.json";

    private string FilePrefix => _entityFolder switch
    {
        "presentations" => "pres",
        "schedules"     => "sched",
        "assignments"   => "assign",
        "drafts"        => "draft",
        _               => "file"
    };

    // ── Shared serializer options ─────────────────────────────────────────────

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
            Console.WriteLine($"Error deserializing entity {id}: {ex.Message}");
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

    /// <summary>
    /// Hard-deletes the entity file.
    /// NOTE: For content entities (presentations, assignments, schedules)
    /// the service layer performs soft-delete by calling
    /// IStorageService.MoveFileAsync directly — it does NOT call this method.
    /// This hard-delete is reserved for cases where permanent removal is
    /// explicitly required (e.g. administrative cleanup).
    /// </summary>
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
