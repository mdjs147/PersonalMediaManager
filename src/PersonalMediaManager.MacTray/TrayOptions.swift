import Foundation

/// 只接受由本机服务进程提供的启动参数。
struct TrayOptions {
    let webUrl: URL
    let parentPid: Int32

    enum ArgumentError: Error, CustomStringConvertible {
        case invalid(String)

        var description: String {
            switch self {
            case .invalid(let message): return message
            }
        }
    }

    static func parse(_ arguments: [String]) throws -> TrayOptions {
        guard arguments.count == 4 else {
            throw ArgumentError.invalid("用法：PersonalMediaManager.MacTray --url http://127.0.0.1:端口/ --parent-pid 父进程编号")
        }

        var values: [String: String] = [:]
        for index in stride(from: 0, to: arguments.count, by: 2) {
            let key = arguments[index]
            guard key == "--url" || key == "--parent-pid", values[key] == nil else {
                throw ArgumentError.invalid("启动参数无效或重复。")
            }
            values[key] = arguments[index + 1]
        }

        guard let rawPid = values["--parent-pid"],
              !rawPid.isEmpty,
              rawPid.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 }),
              let parentPid = Int32(rawPid), parentPid > 1 else {
            throw ArgumentError.invalid("父进程编号必须是大于 1 的有效整数。")
        }

        guard let rawUrl = values["--url"],
              !rawUrl.unicodeScalars.contains(where: { CharacterSet.whitespacesAndNewlines.union(.controlCharacters).contains($0) }),
              var components = URLComponents(string: rawUrl),
              components.scheme?.lowercased() == "http",
              let host = components.host?.lowercased(),
              ["127.0.0.1", "localhost", "::1", "[::1]"].contains(host),
              components.user == nil, components.password == nil,
              let port = components.port, (1...65535).contains(port),
              components.query == nil, components.fragment == nil,
              components.path.isEmpty || components.path == "/" else {
            throw ArgumentError.invalid("WebUI 地址必须是带有效端口的本机 HTTP 根地址，且不得包含凭据、查询或片段。")
        }

        // 将名称固定为回环地址，避免浏览器重新解析 localhost。
        if host == "localhost" { components.host = "127.0.0.1" }
        components.scheme = "http"
        components.path = "/"
        guard let webUrl = components.url else {
            throw ArgumentError.invalid("WebUI 地址无效。")
        }

        return TrayOptions(webUrl: webUrl, parentPid: parentPid)
    }
}
