import Foundation

var checks = 0

func expectValid(_ arguments: [String], expectedUrl: String) {
    do {
        let options = try TrayOptions.parse(arguments)
        guard options.webUrl.absoluteString == expectedUrl, options.parentPid == 123 else {
            fatalError("有效参数的解析结果不正确：\(arguments)")
        }
        checks += 1
    } catch {
        fatalError("有效参数被拒绝：\(arguments)；\(error)")
    }
}

func expectInvalid(_ arguments: [String]) {
    do {
        _ = try TrayOptions.parse(arguments)
    } catch {
        checks += 1
        return
    }
    fatalError("无效参数未被拒绝：\(arguments)")
}

expectValid(["--url", "http://127.0.0.1:9527/", "--parent-pid", "123"], expectedUrl: "http://127.0.0.1:9527/")
expectValid(["--parent-pid", "123", "--url", "http://localhost:80"], expectedUrl: "http://127.0.0.1:80/")
expectValid(["--url", "http://[::1]:65535/", "--parent-pid", "123"], expectedUrl: "http://[::1]:65535/")

for url in [
    "https://127.0.0.1:9527/", "file:///tmp/pmm", "javascript:alert(1)",
    "http://example.com:9527/", "http://127.0.0.1.example.com:9527/",
    "http://0.0.0.0:9527/", "http://192.168.1.1:9527/", "http://[::]:9527/",
    "http://user@127.0.0.1:9527/", "http://user:password@localhost:9527/",
    "http://127.0.0.1:9527/?token=secret", "http://127.0.0.1:9527/#fragment",
    "http://127.0.0.1:9527/path", "http://127.0.0.1/", "http://127.0.0.1:0/",
    "http://127.0.0.1:65536/", "http://127.0.0.1:-1/", "http://127.0.0.1:invalid/",
    " http://127.0.0.1:9527/", "http://127.0.0.1:9527/\n", ""
] {
    expectInvalid(["--url", url, "--parent-pid", "123"])
}

for pid in ["", "0", "1", "-1", "+123", "12.3", "abc", " 123", "2147483648"] {
    expectInvalid(["--url", "http://127.0.0.1:9527/", "--parent-pid", pid])
}

expectInvalid([])
expectInvalid(["--url", "http://127.0.0.1:9527/"])
expectInvalid(["--url", "http://127.0.0.1:9527/", "--parent-pid"])
expectInvalid(["--url", "http://127.0.0.1:9527/", "--parent-pid", "123", "extra"])
expectInvalid(["--url", "http://127.0.0.1:9527/", "--url", "http://127.0.0.1:80/"])
expectInvalid(["--parent-pid", "123", "--parent-pid", "123"])
expectInvalid(["--unknown", "http://127.0.0.1:9527/", "--parent-pid", "123"])

print("参数校验通过：\(checks) 项。")
