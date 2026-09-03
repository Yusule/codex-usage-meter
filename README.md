# Codex Meter

一个 Windows 桌面常驻组件，直接读取当前登录 Codex 账户的额度，显示每个额度池的剩余百分比。

## 使用

1. 确保 Codex 桌面应用已安装并登录。
2. 双击 `Start Codex Meter.vbs`，无需安装依赖。
3. 拖动卡片可调整位置；右上角可以刷新或隐藏窗口；关闭窗口也会收进系统托盘。再次双击启动器会唤醒已有窗口，不会创建第二个实例。

首次启动会等内容完成布局后再把浮窗放到主屏工作区右下角，并保留 20 像素边距。组件每分钟自动刷新一次；内容尺寸变化或重新显示时，如果窗口超出屏幕，程序会自动将它移回可视区域，但不会重置正常的手动拖动位置。

鼠标停在进度条上可查看准确重置时间。切换到其他窗口后，浮窗会自动降低透明度；重新点击浮窗时恢复清晰。

## 数据与隐私

组件调用本机 Codex 随附的只读 `account/rateLimits/read` 协议，不读取、复制或保存 `auth.json`，也不要求 API Key。它显示的是 ChatGPT 账户下 Codex 的滚动使用额度，不是 OpenAI API 的 RPM/TPM 限流。

## 源码

`CodexMeter.ps1` 是当前可直接运行的 Windows/WPF 版本。仓库同时保留了 C#/.NET 版本源码；安装 .NET 8 SDK 后可构建独立程序：

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
