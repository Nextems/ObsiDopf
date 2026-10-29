using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
namespace ObsidianDocs;

public sealed class AuthorProfiles
{
    public ObservableCollection<string> Names { get; } = [];
    public string Selected { get; private set; } = Environment.UserName;
    readonly string? file;
    public AuthorProfiles(string? file = null, bool persist = true)
    {
        this.file = persist ? file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ObsidianDocs","authors.json") : null;
        Names.Add(Environment.UserName);
        try
        {
            if (this.file is not null && File.Exists(this.file))
            {
                var data=JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(this.file));
                foreach (var name in data?.Names ?? []) Add(name,false);
                if (data is not null && Names.Contains(data.Selected)) Selected=data.Selected;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void Choose(string name)
    {
        name=Normalize(name); Add(name,false); Selected=name; Save();
    }
    public void Add(string name,bool save=true)
    {
        name=Normalize(name); if (!Names.Contains(name)) Names.Add(name); if (save) Save();
    }
    static string Normalize(string name)
    {
        name=name.Trim();
        if (name.Length is <1 or >100 || name.Any(char.IsControl)) throw new ArgumentException("Имя автора должно содержать от 1 до 100 символов без переносов строк.");
        return name;
    }
    void Save()
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp=file+"."+Guid.NewGuid()+".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(new ProfileData(Names.ToArray(),Selected))); File.Move(temp,file,true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public sealed record ProfileData(string[] Names,string Selected);
}
