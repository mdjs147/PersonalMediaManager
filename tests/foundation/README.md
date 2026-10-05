# DEV001 fresh foundation tests

这些测试全部新写，只验 DEV001 离线工程骨架，不采用旧业务测试、旧库或历史通过数。基础测试通过不代表任何业务能力、Windows/macOS 或生产安全策略通过。

## 覆盖与边界

- xUnit：只接受精确端口参数，完整端点集合仅 `/` 和 `/health` 的 GET/HEAD；拒绝业务 worker、HTTP client、DataProtection 密钥服务和密钥操作；配置污染不能启用 Development 或覆盖监听
- 新临时目录：唯一、初始为空，数据库尚不存在；自身清理幂等且不删除别的测试目录；遇到子目录、文件或根目录 symlink/reparse point 必须拒绝并保留证据
- 新 SQLite：每例使用 `IsolatedTestWorkspace.Create()` 创建的目录，`Pooling=False`，测试专用 Probe DbContext 只做空库建表与 roundtrip；connection/context 先于目录释放
- Python：启动实际编译产物，通过 Linux `/proc` 的子进程 socket inode 验证唯一 IPv4 loopback 监听、惯性 Razor/health、写方法和未知业务路径拒绝、隔离 HOME/XDG/运行目录文件无变更及正常关闭

Python 的 socket 检查是重复观察，不宣称证明整个进程历史中绝无外连；完整端点和服务注册由独立 xUnit 断言补充。无真实媒体、旧数据库、服务、账户、凭据或外网业务调用。包恢复与编译由集成任务处理。

## 用例对应风险

- `NoArgumentsSelectsAnEphemeralPort`：默认绑定固定端口导致误连或冲突
- `OnlyAnExplicitNumericPortIsAccepted`：合法端口边界解析错误；4组数据分别覆盖0、最小正数、最大值与前导零
- `AmbiguousOrUnsupportedConfigurationIsRejected`：非法端口、重复参数、环境/地址/数据路径注入；每组输入对应一种拒绝边界
- `CompleteEndpointSetContainsOnlyReadOnlyShellAndHealth`：意外暴露业务路由或写方法；仅将 MVC 空根模板规范化为 `/`
- `HostDoesNotInstallBusinessWorkersOrNetworkClients`：启动自定义任务或提供未授权网络客户端
- `DataProtectionCannotCreateOrConsumeKeys`：Razor依赖隐式创建/使用持久密钥
- `EnvironmentCannotEnableDevelopmentOrOverrideBinding`：外部配置启用开发环境或覆盖固定监听策略
- `CreationIsUniqueEmptyAndDoesNotCreateADatabase`：复用既有测试目录或隐式建库
- `DisposalOnlyRemovesItsOwnWorkspaceAndIsIdempotent`：误删别的测试数据或重复释放失败
- `DisposalRefusesChildLinksWithoutTouchingTheirTargets`：文件/目录链接逃逸及拒绝前部分删除证据
- `DisposalRefusesAReplacedRootLink`：根目录被换为链接后错误递归清理
- `FreshSqliteRoundTripIsConfinedAndIndependent`：数据库串用、残余连接池及读写仅存在于伪造测试
- `verify_foundation.py`：实际进程绑定、响应、关闭与 HOME/XDG/运行目录副作用偏离容器检查；快照同时记录目录和文件

小改只运行相关用例；最终集成统一全量验证，不以额外通过数扩张业务验收。

## 执行

先按项目已评审 SDK 环境设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，完成带锁官方 NuGet 恢复及构建，再运行：

```sh
dotnet test tests/foundation/PersonalMediaManager.Foundation.Tests.csproj --no-build --no-restore
python3 tests/foundation/verify_foundation.py
```

脚本默认使用 Debug/net10.0；Release 构建需加 `--configuration Release`。缺少编译产物、Linux `/proc`、有效断言或正常退出均失败，不静默跳过。每次运行只创建新的系统临时目录。

English: Newly authored xUnit and actual-process tests cover only the inert Linux foundation. Temporary SQLite data is fresh and unpooled. Product behavior, other platforms and production security remain not-run. No historical implementation, data or passing count is adopted.
