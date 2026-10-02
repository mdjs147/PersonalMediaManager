# macOS 原生菜单栏辅助程序

此目录使用 Swift + AppKit 实现菜单栏常驻入口，不引入跨平台 UI 框架，也不承载服务器或业务逻辑。服务仍由 `PersonalMediaManager.Server` 托管；辅助程序显示运行状态、打开本机 WebUI，并通知服务退出。

## 构建与部署

在装有 Xcode Command Line Tools 的 macOS 机器上，从仓库根目录执行：

```bash
# 先将 Server 发布至目标目录，再构建同架构的原生辅助程序。
bash scripts/build-macos-tray.sh ./dist/osx-arm64 arm64
# Intel Mac：
bash scripts/build-macos-tray.sh ./dist/osx-x64 x64
```

脚本只编译辅助程序，不执行 `dotnet publish`。省略架构参数时使用当前机器架构；输出文件固定为目标目录下的 `PersonalMediaManager.MacTray`，需与 Server 可执行文件相邻并保留执行权限。Swift 编译部署目标为 macOS 13；运行整个产品还须满足 .NET 10 的操作系统要求。无须 Swift Package Manager、额外 NuGet 包或 AppKit 工作负载。

此辅助程序须由已登录的 macOS 图形桌面会话启动；SSH、容器或系统后台服务应使用 Server 的无托盘模式。脚本不签名、不公证，不生成 `.app` 包；正式对外分发时需要另行处理 Apple 签名、公证与 Gatekeeper 验证。

## 父子进程协议

父进程应在 HTTP 服务成功启动后，通过直接创建进程的方式启动：

```text
PersonalMediaManager.MacTray --url http://127.0.0.1:9527/ --parent-pid 12345
```

- 两个参数必须各出现一次，可交换顺序；其他参数均拒绝
- `--parent-pid` 必须是实际创建本辅助程序的进程编号且大于 1；不要经过 `open`、Shell 包装器或其他改变父子关系的中间进程
- `--url` 仅接受带 `1..65535` 端口的 HTTP 根地址；主机仅允许 `127.0.0.1`、`localhost` 或 `::1`，拒绝凭据、查询、片段及子路径；`localhost` 会固定为 `127.0.0.1`
- 父进程应重定向并持续读取 stdout；用户选择“退出 PersonalMediaManager”后，辅助程序仅写入一次 UTF-8 `quit\n`，然后退出；父进程收到后执行正常停机
- stdout 不含日志或其他协议消息；诊断错误只写 stderr，参数无效时退出码为 `64`
- 辅助程序每秒检查实际父进程编号；父进程正常退出、异常退出或被强制终止后，辅助程序会自动退出，且不会发送 `quit`；父进程编号复用不会让旧托盘继续存活
- 父进程保持 stdin 管道打开；EOF 时辅助程序回主线程正常退出。不会向父进程发送信号；意外退出与收到 `quit` 应由父进程区分处理
- AppKit 初始化与事件循环运行在主线程；菜单打开期间依然检查父进程；使用 accessory 激活策略，不创建窗口或 Dock 图标
- “服务运行中”表示父服务进程正在运行，辅助程序本身不执行 HTTP 健康检查

## 验证

### 参数单元检查

以下命令在 macOS 编译并运行独立的参数测试，不启动图形界面：

```bash
test_dir="$(mktemp -d)"
xcrun --sdk macosx swiftc -swift-version 5 \
  src/PersonalMediaManager.MacTray/TrayOptions.swift \
  src/PersonalMediaManager.MacTray/tests/main.swift \
  -o "$test_dir/MacTrayArgumentTests"
"$test_dir/MacTrayArgumentTests"
rm -rf "$test_dir"
```

检查范围包括 IPv4/IPv6 回环地址、localhost 规范化、参数顺序、缺项/重复项，以及外部地址、非 HTTP 协议、凭据、无效端口、路径、查询、片段和无效父进程编号。

### macOS 图形桌面人工验收

1. 发布并启动 Server，确认服务启动后出现唯一的菜单栏图标，没有额外主窗口或 Dock 图标
2. 展开菜单，确认运行状态、打开 WebUI、退出三项显示正常；重复打开 WebUI，确认默认浏览器始终访问指定回环地址
3. 选择退出，确认父服务正常停机，端口释放，菜单栏图标消失；再次启动后不残留旧进程
4. 分别正常关闭、强制结束父 Server，确认辅助程序约一秒内退出；展开菜单时重复此检查
5. 单独传入错误父进程编号或外部 URL，确认返回 `64` 且没有菜单栏图标；使用 `--headless` 启动服务，确认不生成辅助进程
6. 在 Apple Silicon 和 Intel 对应架构分别执行构建及上述检查；验证打包后执行权限、Gatekeeper 行为与签名要求

当前实现环境为 Linux，未提供 Swift/macOS SDK 或图形桌面，因此不能在这里编译或执行 AppKit，也不能宣称上述原生测试通过。需在 macOS 上完成参数测试、原生构建与菜单交互验收。

## 原生 API 依据

- [Apple NSApplication](https://developer.apple.com/documentation/appkit/nsapplication)
- [Apple NSStatusItem](https://developer.apple.com/documentation/appkit/nsstatusitem)
- [Apple NSWorkspace](https://developer.apple.com/documentation/appkit/nsworkspace)
