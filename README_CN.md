# Jasnote

<p align="center">
  <a href="https://github.com/Elykdez/Jasnote"><img src="src\Resources\Icons\logo.png" alt="Jasnote" width="96" /></a>
</p>
<p align="center">
  <p align="center"><strong>打开和查看超大型 JSON | JSONL 文档的桌面应用。</strong></p>
</p>
<p align="center">
  <a href="./README.md">English</a> | 中文
</p>
<p align="center">
  <a href="https://github.com/Elykdez/Jasnote/actions/workflows/ci-cd.yml"><img alt="CI/CD" src="https://github.com/Elykdez/Jasnote/actions/workflows/ci-cd.yml/badge.svg" /></a>
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4" />
  <img alt="WinForms" src="https://img.shields.io/badge/UI-WinForms-0078D4" />
  <img alt="JSON" src="https://img.shields.io/badge/data-JSON-2F7D32" />
  <img alt="Windows" src="https://img.shields.io/badge/platform-Windows-0078D6" />
  <a href="https://github.com/Elykdez/Jasnote/releases"><img alt="Release" src="https://img.shields.io/github/v/release/Elykdez/Jasnote?label=release" /></a>
</p>

> 受到 [Janice](https://github.com/ErikKalkoken/Janice) 启发。

- 使用自定义虚拟化树视图，因此浏览大型文件时不需要为每个 JSON 元素创建一个 UI 节点。

## 功能

- 以可展开的树结构浏览 JSON 数据。
- 通过文件选择器、剪贴板、拖放或命令行参数打开文件。
- 使用通配符模式搜索键和值。
- 将选中的 JSON 分支导出到新文件，或复制到剪贴板。
- 查看超大型 JSON 文件，例如超过 2GB、包含 100MB+ 元素的文档，同时避免严重的内存压力或 UI 卡顿。
- 支持浅色和深色主题。
- 支持切换界面语言（英语，简体中文）。

## 要求

- Windows 10，且已安装 .NET Desktop Runtime 8 或更高版本。
- 从源码构建时需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本。

## 运行 (Windows)

从 [Releases 页面](https://github.com/Elykdez/jasnote/releases)下载最新打包版本，解压后运行解压目录中的 `run.bat`。

从源码运行：

```powershell
.\run.bat
```

也可以直接打开一个文件：

```powershell
.\run.bat .\sample.json
```

等价的 `dotnet` 命令：

```powershell
dotnet run --project .\src\Jasnote.csproj -- .\sample.json
```

## 构建

还原依赖：

```powershell
dotnet restore .\src\Jasnote.csproj
```

构建 Release 二进制文件：

```powershell
dotnet build .\src\Jasnote.csproj -c Release
```

构建输出会写入：

```text
src\bin\Release\net8.0-windows\
```

如果你的 SDK 支持 `.slnx`，也可以构建解决方案文件：

```powershell
dotnet build .\Jasnote.slnx -c Release
```

## 计划

- 改进 UI
- 添加 OSX 支持
- 添加更多本地化
- 添加更多 JSON 相关工具

## 致谢

- GPT 5.5 协助撰写部分代码和文档。
