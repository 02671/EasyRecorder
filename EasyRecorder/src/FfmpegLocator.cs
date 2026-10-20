using System.Diagnostics;

namespace EasyRecorder;

internal static class FfmpegLocator
{
    public static string? Find(AppSettings settings)
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            settings.FfmpegPath ?? string.Empty,
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(baseDir, "bin", "ffmpeg.exe")
        };

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (string folder in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    public static bool Validate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(path, "-hide_banner -version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Warmup(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("lavfi");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add("color=s=16x16:d=0.04");
            process.StartInfo.ArgumentList.Add("-an");
            process.StartInfo.ArgumentList.Add("-c:v");
            process.StartInfo.ArgumentList.Add("libx264");
            process.StartInfo.ArgumentList.Add("-preset");
            process.StartInfo.ArgumentList.Add("ultrafast");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("null");
            process.StartInfo.ArgumentList.Add("-");
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public static string? Ensure(IWin32Window owner, AppSettings settings)
    {
        string? found = Find(settings);
        if (found != null && File.Exists(found))
        {
            if (settings.FfmpegPath != found)
            {
                settings.FfmpegPath = found;
                settings.Save();
            }

            return found;
        }

        DialogResult ask = MessageBox.Show(
            owner,
            "没有找到可用的 ffmpeg.exe。\n\n" +
            "FFmpeg 是本程序用来生成 MP4 的组件。\n" +
            "你可以把 ffmpeg.exe 放到本程序同一个文件夹里，\n" +
            "也可以现在点击「是」手动选择它。\n\n" +
            "要现在选择 ffmpeg.exe 吗？",
            "需要 FFmpeg",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (ask != DialogResult.Yes)
        {
            return null;
        }

        using OpenFileDialog dialog = new()
        {
            Title = "请选择 ffmpeg.exe",
            Filter = "FFmpeg 程序 (ffmpeg.exe)|ffmpeg.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        if (!Validate(dialog.FileName))
        {
            MessageBox.Show(
                owner,
                "这个文件不能正常运行，可能不是 Windows 版的 ffmpeg.exe。\n请换一个文件再试。",
                "FFmpeg 不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        settings.FfmpegPath = dialog.FileName;
        settings.Save();
        return dialog.FileName;
    }
}
