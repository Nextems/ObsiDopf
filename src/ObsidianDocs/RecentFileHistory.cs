using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsidianDocs;

internal sealed record RecentFileEntry(string FilePath,DateTimeOffset LastOpenedUtc)
{
    [JsonIgnore] public string Name=>Path.GetFileName(FilePath) is { Length:>0 } name ? name : FilePath;
    [JsonIgnore] public string Folder=>Path.GetDirectoryName(FilePath) ?? FilePath;
    [JsonIgnore] public string OpenedLabel=>LastOpenedUtc==default ? "" : LastOpenedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

internal sealed class RecentFileHistory
{
    sealed record HistoryData(int Version,List<RecentFileEntry> Files);
    readonly string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ObsidianDocs","recent-files.json");
    readonly string mutexName;

    public RecentFileHistory()
    {
        mutexName="Local\\ObsiDopf.RecentFiles."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
    }

    public Task<IReadOnlyList<RecentFileEntry>> ReadAsync()=>Task.Run(()=>Locked<IReadOnlyList<RecentFileEntry>>(Read));

    public Task RememberAsync(string path)
    {
        path=Normalize(path);
        return Task.Run(()=>Locked(()=>
        {
            var files=Read();
            files.RemoveAll(entry=>entry.FilePath.Equals(path,StringComparison.OrdinalIgnoreCase));
            files.Insert(0,new RecentFileEntry(path,DateTimeOffset.UtcNow));
            Write(files);
            return true;
        }));
    }

    public Task RemoveAsync(string path)=>Task.Run(()=>Locked(()=>
    {
        var files=Read();
        if(files.RemoveAll(entry=>entry.FilePath.Equals(path,StringComparison.OrdinalIgnoreCase))>0) Write(files);
        return true;
    }));

    public Task ClearAsync()=>Task.Run(()=>Locked(()=> { Write([]); return true; }));

    T Locked<T>(Func<T> action)
    {
        // Shell file associations can start several application instances.
        // Merge with the current on-disk history under a per-user mutex.
        // All waiting and file I/O stay on background threads.
        using var mutex=new Mutex(false,mutexName);
        bool owned=false;
        try
        {
            try { owned=mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch(AbandonedMutexException) { owned=true; }
            if(!owned) throw new TimeoutException("История занята другим экземпляром ObsiDopf. Повторите действие.");
            return action();
        }
        finally { if(owned) mutex.ReleaseMutex(); }
    }

    List<RecentFileEntry> Read()
    {
        string json;
        try { json=File.ReadAllText(file); }
        catch(FileNotFoundException) { return []; }
        catch(DirectoryNotFoundException) { return []; }
        var data=JsonSerializer.Deserialize<HistoryData>(json);
        if(data is null || data.Version!=1 || data.Files is null)
            throw new InvalidDataException("Не удалось прочитать формат истории. Существующий список не изменён.");
        var valid=new List<RecentFileEntry>();
        foreach(var entry in data.Files)
        {
            if(entry is null || string.IsNullOrWhiteSpace(entry.FilePath) || !Path.IsPathFullyQualified(entry.FilePath)) continue;
            try { valid.Add(entry with { FilePath=Normalize(entry.FilePath) }); }
            catch(ArgumentException) { }
            catch(NotSupportedException) { }
            catch(PathTooLongException) { }
        }
        return valid.GroupBy(entry=>entry.FilePath,StringComparer.OrdinalIgnoreCase)
            .Select(group=>group.OrderByDescending(entry=>entry.LastOpenedUtc).First())
            .OrderByDescending(entry=>entry.LastOpenedUtc).ToList();
    }

    static string Normalize(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан путь к документу.");
        return Path.GetFullPath(path);
    }

    void Write(List<RecentFileEntry> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(new HistoryData(1,files)));
            File.Move(temporary,file,true);
        }
        finally
        {
            try { if(File.Exists(temporary)) File.Delete(temporary); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
    }
}
