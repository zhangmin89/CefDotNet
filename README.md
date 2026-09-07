# CefDotNet

## 一阶段 fork CefGlue    cef 120

以 master@837440e  Commits on Oct 22, 2025 为基线
多AI 审核代码

``` text

CefGlue.Avalonia / CefGlue.WPF
                │             UI层实现
                ▼
            CefGlue.Common
                │
                |             UI 无关浏览器实现
                │             Runtime / Handler / 生命周期 / JS
                |
                ▼
    CefGlue.Common.Shared
                │               IPC
                ▼
            Xilium.CefGlue
                |           纯 .NET Binding
                ↓
                CEF

CefGlue.BrowserProcess
              │
              ├──→ CefGlue.Common.Shared
              └──→ Xilium.CefGlue

```

| 测试项目 | 类型 | 特点 |
|---|---|---|
| `CefGlue.Tests` | 核心单元测试 | 覆盖序列化、IPC、JS 绑定、Runtime、Adapter 等 |
| `CefGlue.Avalonia.Tests` | Avalonia + CEF 集成测试 | 启动真实 UI 线程和浏览器；本地构建 BrowserProcess；Release 下 30 秒超时 |
| `CefGlue.WPF.Tests` | WPF 控件测试 | 使用 STA 线程，主要测试控件、Popup 和离屏 Host |

实际打包项目

- CefGlue.Common
- CefGlue.Avalonia
- CefGlue.WPF
- CefGlue.Common.ARM64
- CefGlue.Avalonia.ARM64
- CefGlue.WPF.ARM64

核心打包逻辑位于 CefGlue.Common.csproj：

打包时为 Windows、Linux、macOS 发布对应架构的自包含 BrowserProcess。
BrowserProcess 被放进包内的 tools/browser-process/<rid>。
Xilium.CefGlue.dll 和 Common.Shared 也合并进 Common 包。
随包携带 buildTransitive 配置，消费端构建时会校验 RID/架构，并把 CEF 和 BrowserProcess 复制到输出目录的 CefGlueBrowserProcess 下。
支持 win/linux/osx × x64/arm64 六个 RID。

## 二阶段 调整并升级 cef 到 134

### ，调整关系,升级工程，审核代码

####
调整AssemblyName，PackageId
现有文件命名空间不动，
新增的文件使用新的命名空间


####

C# 代码跨平台且AnyCPU
去除操作系统和架构
```

             win-x64
             win-arm64
             linux-x64
Package ───── linux-arm64
             osx-x64
             osx-arm64

```

CefDotNet
CefDotNet.Avalonia


CI

测试
Nuget pack
Nuget push

多平台
