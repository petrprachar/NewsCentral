using System.Text.Json;
using NewsCentral.Models;

namespace NewsCentral.Repositories;

public class JsonFileRepository<T> : IRepository<T> where T : class, IEntity
{
    private readonly string _basePath;
    private readonly string _entityFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileRepository(string basePath, string entityFolder)
    {
        _basePath = basePath;
        _entityFolder = entityFolder;

        // Ensure base directory exists
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(Path.Combine(_basePath, _entityFolder));
    }

    public async Task<List<T>> GetAllAsync()
    {
        var folderPath = Path.Combine(_basePath, _entityFolder);

        if (!Directory.Exists(folderPath))
            return new List<T>();

        var files = Directory.GetFiles(folderPath, "*.json");
        var entities = new List<T>();

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var entity = JsonSerializer.Deserialize<T>(json);
                if (entity != null)
                    entities.Add(entity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {file}: {ex.Message}");
            }
        }

        return entities;
    }

    public async Task<T?> GetByIdAsync(string id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading {filePath}: {ex.Message}");
            return null;
        }
    }

    public async Task<T> CreateAsync(T entity)
    {
        await _lock.WaitAsync();
        try
        {
            var filePath = GetFilePath(entity.GetId());

            if (File.Exists(filePath))
                throw new InvalidOperationException($"Entity {entity.GetId()} already exists");

            var json = JsonSerializer.Serialize(entity, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            await File.WriteAllTextAsync(filePath, json);

            return entity;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> UpdateAsync(T entity)
    {
        await _lock.WaitAsync();
        try
        {
            var filePath = GetFilePath(entity.GetId());

            if (!File.Exists(filePath))
                throw new InvalidOperationException($"Entity {entity.GetId()} does not exist");

            var json = JsonSerializer.Serialize(entity, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            await File.WriteAllTextAsync(filePath, json);

            return entity;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var filePath = GetFilePath(id);

            if (!File.Exists(filePath))
                return false;

            File.Delete(filePath);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string id)
    {
        var filePath = GetFilePath(id);
        return await Task.FromResult(File.Exists(filePath));
    }

    private string GetFilePath(string id)
    {
        var fileName = $"{id}.json";
        return Path.Combine(_basePath, _entityFolder, fileName);
    }
}