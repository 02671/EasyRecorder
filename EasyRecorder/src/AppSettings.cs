using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyRecorder;

public enum AudioMode
{
    None = 0,
    System = 1,
    Microphone = 2,
    Both = 3
}

public sealed class AppSettings
{
    public string OutputFolder { get; set; } = DefaultOutputFolder();
    public AudioMode AudioMode { get; set; } = AudioMode.Both;
    public bool WelcomeShown { get; set; }

    [JsonIgnore]
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyRecorder");

    [JsonIgnore]
    private static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    public static string DefaultOutputFolder()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(videos, "EasyRecorder");
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.OutputFolder))
                    {
                        loaded.OutputFolder = DefaultOutputFolder();
                    }

                    return loaded;
                }
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
