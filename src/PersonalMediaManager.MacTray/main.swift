import AppKit
import Darwin

/// 仅负责原生菜单栏与父进程通知，不承载服务或业务逻辑。
@MainActor
final class TrayDelegate: NSObject, NSApplicationDelegate {
    private let options: TrayOptions
    private var statusItem: NSStatusItem?
    private var parentTimer: Timer?
    private var quitRequested = false

    init(options: TrayOptions) {
        self.options = options
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard getppid() == options.parentPid else {
            NSApplication.shared.terminate(nil)
            return
        }

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let image = NSImage(systemSymbolName: "play.rectangle", accessibilityDescription: "PersonalMediaManager") {
            image.isTemplate = true
            item.button?.image = image
        } else {
            item.button?.title = "PMM"
        }
        item.button?.toolTip = "PersonalMediaManager · 服务运行中"

        let menu = NSMenu()
        menu.autoenablesItems = false
        let status = NSMenuItem(title: "服务运行中", action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        menu.addItem(.separator())

        let open = NSMenuItem(title: "打开 WebUI", action: #selector(openWebUi), keyEquivalent: "o")
        open.target = self
        menu.addItem(open)

        let quit = NSMenuItem(title: "退出 PersonalMediaManager", action: #selector(quitServer), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        item.menu = menu
        statusItem = item

        // 父进程退出后会被重新收养；检查实际父进程可避免编号复用留下孤儿托盘。
        // common 模式确保展开菜单时也继续检查，所有 AppKit 操作仍在主线程。
        let timer = Timer(timeInterval: 1, target: self, selector: #selector(checkParent), userInfo: nil, repeats: true)
        parentTimer = timer
        RunLoop.main.add(timer, forMode: .common)

        // 父进程关闭 stdin 表示正常停止；后台只读管道，AppKit 收尾仍回主线程。
        DispatchQueue.global(qos: .utility).async {
            while !FileHandle.standardInput.availableData.isEmpty { }
            DispatchQueue.main.async {
                NSApplication.shared.terminate(nil)
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        parentTimer?.invalidate()
        parentTimer = nil
        if let item = statusItem {
            NSStatusBar.system.removeStatusItem(item)
            statusItem = nil
        }
    }

    @objc private func openWebUi() {
        guard getppid() == options.parentPid else {
            NSApplication.shared.terminate(nil)
            return
        }
        if !NSWorkspace.shared.open(options.webUrl) {
            writeError("无法打开默认浏览器，请手动访问服务的本机地址。")
        }
    }

    @objc private func quitServer() {
        guard !quitRequested else { return }
        quitRequested = true

        // 标准输出仅用于 IPC；直接写入避免行缓冲导致父进程收不到退出请求。
        do {
            try FileHandle.standardOutput.write(contentsOf: Data("quit\n".utf8))
        } catch {
            writeError("无法通知父进程退出。")
        }
        NSApplication.shared.terminate(nil)
    }

    @objc private func checkParent() {
        if getppid() != options.parentPid {
            NSApplication.shared.terminate(nil)
        }
    }
}

private func writeError(_ message: String) {
    try? FileHandle.standardError.write(contentsOf: Data((message + "\n").utf8))
}

// 显式主执行器入口保证初始化、事件循环及委托生命周期都在 AppKit 主线程。
@main
private struct TrayMain {
    @MainActor
    static func main() {
        let options: TrayOptions
        do {
            options = try TrayOptions.parse(Array(CommandLine.arguments.dropFirst()))
            guard getppid() == options.parentPid else {
                throw TrayOptions.ArgumentError.invalid("父进程编号与实际启动进程不一致。")
            }
        } catch {
            writeError(String(describing: error))
            exit(64)
        }

        // 父进程关闭管道时写入失败即可，不让 SIGPIPE 直接终止菜单回调。
        signal(SIGPIPE, SIG_IGN)

        // AppKit 必须在进程主线程启动；accessory 不显示 Dock 图标或主窗口。
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        let delegate = TrayDelegate(options: options)
        application.delegate = delegate
        application.run()
        withExtendedLifetime(delegate) {}
    }
}
