# EasyRecorder

简单好用的 Windows 录屏工具。深色横向工具栏，支持框选区域、系统声 / 麦克风、MP4（H.264）+ AAC。

## 下载

最新 Windows 版（64 位，双击即可运行）：

https://github.com/02671/EasyRecorder/releases/latest

把 `ffmpeg.exe` 放到 `EasyRecorder.exe` 同一个文件夹后，双击运行。

## 使用

1. 把 `ffmpeg.exe` 放到程序同目录（只需一次）。
2. 双击 `EasyRecorder.exe`。
3. 点「区域」框选画面，或直接点「录制」录主屏幕。
4. 点「结束」保存；点「取消」不保存。

录像默认保存在「视频」文件夹里的 EasyRecorder 目录。

## 自行编译

需要 .NET 8 SDK：

```bash
dotnet publish EasyRecorder/EasyRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

输出文件：`publish/EasyRecorder.exe`
