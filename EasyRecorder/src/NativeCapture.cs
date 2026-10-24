using System.Reflection;

namespace EasyRecorder;

internal static class NativeCapture
{
    public static void DeployLibrary()
    {
        byte[]? payload = ReadPayload();
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            TryWrite(Path.Combine(exeDir, "ScreenRecorderLib.dll"), payload);
        }

        string localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyRecorder");
        try
        {
            Directory.CreateDirectory(localDir);
            TryWrite(Path.Combine(localDir, "ScreenRecorderLib.dll"), payload);
        }
        catch
        {
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            AssemblyName name = new(args.Name);
            if (!string.Equals(name.Name, "ScreenRecorderLib", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string localPath = Path.Combine(localDir, "ScreenRecorderLib.dll");
            if (File.Exists(localPath))
            {
                return Assembly.LoadFrom(localPath);
            }

            return null;
        };
    }

    private static byte[]? ReadPayload()
    {
        using Stream? stream = typeof(NativeCapture).Assembly.GetManifestResourceStream(
            "EasyRecorder.ScreenRecorderLib.dll");
        if (stream == null)
        {
            return null;
        }

        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void TryWrite(string path, byte[] payload)
    {
        try
        {
            File.WriteAllBytes(path, payload);
            return;
        }
        catch
        {
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
