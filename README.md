# EasyRecorder

简单好用的 Windows 录屏工具。深色横向工具栏，支持框选区域、系统声 / 麦克风、MP4（H.264）+ AAC。

## 下载

最新 Windows 版（64 位）：

https://github.com/02671/EasyRecorder/releases/latest

本机需已安装 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。双击 `EasyRecorder.exe` 即可使用，无需安装 FFmpeg。

## 使用

1. 双击 `EasyRecorder.exe`。
2. 点「区域」框选画面，或直接点「录制」录主屏幕。
3. 点「结束」保存；点「取消」不保存。

录像默认保存在「视频」文件夹里的 EasyRecorder 目录。

首次使用可能需要安装 [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)。Windows N / KN 版还需安装媒体功能包。

## 自行编译

需要 .NET 8 SDK：

```bash
dotnet publish EasyRecorder/EasyRecorder.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

输出文件：`publish/EasyRecorder.exe`
