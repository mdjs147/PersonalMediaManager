# DEV002冻结离线合同（中文权威）

状态：planned；本合同不激活实现。输入来源为rebuild-mapping登记的只读domain-model等contracts设计，尤其§6.2的episode精确小数；不导入旧源码、测试或验收。下列具体输入上限、receipt和预览规则是本卡明示工程定义，不是新的外部生产策略。

## 1. 身份、模型与输入

- WorkId、NumberingSystemId、EpisodeId、CoverageId、RequestId均为非空GUID；身份不由标题或序号推导
- WorkKind仅Movie/Series；标题非空且非纯空白，最多512字符，原样保存；Episode可选标题允许null/空，非null最多512字符；不新增描述字段
- Series创建时恰有一个显式manual NumberingSystemId，Movie必须没有体系、季集或覆盖；WorkId、WorkKind和Series体系ID创建后不变。数据库关系也须拒绝Movie挂体系及跨work/system引用，不冻结表数量
- SeasonNumber为0至Int32.MaxValue。0可人工登记，不从OVA/SP或外部规则猜出季号
- ExactDecimalOrdinal只用于episode：ASCII [0-9]+(\.[0-9]+)?，整数和小数部分各最多28位，总原文最多57字符。保存原文，去整数前导0、小数尾随0，统一纯零为0；12.500与12.5同规范键。负号、指数、locale分隔符、空白、超限明确拒绝；不经float/REAL/native decimal隐式舍入。比较按精确数值，不能用SQLite REAL或普通字符串排序
- EpisodeKey=(SeasonNumber,ExactDecimalOrdinal)；EpisodeRecord含ID、所属system、key、可选title。唯一键是(WorkId,NumberingSystemId,SeasonNumber,CanonicalEpisodeOrdinal)
- CoverageRecord含ID、所属system和显式有序EpisodeId成员；存在coverage时成员非空、无重复、全属同work/system。允许不连续与跨季成员，不补中间项；重排不改变CoverageId或成员集合，但展示顺序往返保真。数据库至少约束(coverage,episode)与(coverage,ordinal)双唯一和复合外键
- WorkSnapshot包含work身份/type/title、manual system（Movie为空）、revision、episodes与coverage。快照及返回receipt不可因调用者修改输入集合而改变

- Episodes、Coverage、Assignments、Members集合以及其元素/必填key为null时明确拒绝，不能把null替换请求当作允许的empty并清空旧图；仅Episode.Title与Movie.SystemId保留上述null例外

## 2. 命令、查询与事务

- CreateWork(RequestId,WorkId,Kind,Title,NumberingSystemId?)成功revision=1；同名不同WorkId合法
- ReplaceManualCatalog(RequestId,WorkId,ExpectedRevision,NumberingSystemId?,Title,Episodes,Coverage)只替换该work的可变聚合，显式system须与不可变work体系一致（Movie为null），空catalog也验证此绑定；允许空catalog，已有coverage的成员仍须非空。每次成功的新mutation恰好+1；long.MaxValue明确拒绝，不转REAL或回绕
- Get/List从库一致回读；同一WorkSnapshot的revision/root/children必须来自单一read transaction。顺序确定，episode按精确季集值排序，coverage显式成员顺序保留
- 先完成格式/规范化校验并冻结命令，指纹和后续写入使用同一份不可变表示。每次写入用短事务，先查已存在RequestId并重放/冲突；仅新请求再查当前引用并以ExpectedRevision做CAS，状态与成功receipt同事务提交。同一Work及ExpectedRevision的真实两连接竞争最多一条新写成功；BUSY/LOCKED是独立存储失败，不得冒充revision冲突或成功
- RequestId作用域是整个单一隔离Catalog库；规范指纹覆盖全部命令字段：命令种类、WorkId、ExpectedRevision（适用时）、Kind（仅Create）、Work Title、NumberingSystemId（含episode/coverage归属）、Episode可选title（null与empty不同）、规范key、实体ID、成员和展示顺序。episode/coverage记录列表按ID规范排序，coverage成员顺序保留；唯一排除项是episode原文字形，不从当前DB补Kind、title、revision或其他可变字段
- 同token同规范请求返回第一次持久化的完整receipt/snapshot/revision和原文字形，即使work后来已变；12.500与12.5可重放。相同token不同规范请求明确冲突且零变更；换token可显式更新字形并正常递增revision
- 失败且完整回滚的命令不占用token，不保存失败受理状态。无通用Sqlite异常可被当成幂等成功；失败不能留下半聚合或receipt
- 对外只有OfflineCatalogDatabase.CreateFresh()创建自己的唯一新临时目录/新文件，不接受外部path或connection string；OpenSession()提供独立真实连接，Pooling=false，外键开启。只用新库，不访问旧/真实媒体路径。关闭资源不擅自清除仍需保留的测试证据；测试专用故障只能在新测试库中注入，产品不能增加fault hook

## 3. 纯编号预览

ManualNumberingPreview.Build(snapshot,request)；request包含WorkId、NumberingSystemId、ExpectedRevision及至少一项有序(SourceEpisodeId,TargetEpisodeKey)。只支持Series，三个绑定值必须匹配给定snapshot；源ID必须存在且同work/system。拒绝重复源、重复规范目标及与未映射现有集的目标冲突。先整体腾出本次源集原key，因此完整互换合法。

返回每项原key、显式人工目标key及三个绑定值/明确问题，保留调用方显式次序。不解析文件名、不猜累计、不展开含糊区间、不引用repository，不写snapshot/database/receipt/revision。不保证给定snapshot仍是实时最新，没有采用入口。

## 4. 八组风险与完成条件

1. 无外部ID可创建；同名作品身份不同；空GUID/非法类型拒绝；Movie不能挂system/季集/coverage
2. 原文/规范键、零、小数等价、精确排序、大精度和边界拒绝，无截断/舍入/locale依赖
3. 跨季和不连续集合往返保真；成员顺序可变但身份/集合不被范围化；重复/空coverage成员拒绝
4. 跨work/system、重复规范key及重复成员/ordinal由服务和真实数据库约束拒绝；无需冻结表数
5. 陈旧revision拒绝，两个真实SQLite连接通过并发入口竞争只一个提交；一致快照不混版本；BUSY/LOCKED分类准确
6. 同token同规范重放与异载荷冲突；原receipt在后来revision变化后仍保真；新库SQL trigger在看见新revision及子行后于receipt写入产生实际失败，证明整事务回滚且token不占用，不用产品fault hook
7. 预览验证work/system/revision、源/目标冲突与合法互换；前后snapshot、数据库、receipt和revision不变；不把内存匹配称实时最新
8. 锁定restore/build/非空新测试及既有惯性宿主检查；host仍仅根页/health，无Catalog生产路由、额外监听、worker、真实媒体/旧库/外联或凭据副作用

必要结果均绑定当前卡精确候选、实际命令/退出码和真实独审。新单元/SQLite集成测试从零编写；旧工程基座作为回归依赖，不冒充新Catalog或完整UI/HTTP/MCP验收。中文/英文变更以真实Git字节独立语义审查闭合，未验项继续draft/not-run。
