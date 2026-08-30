using Xunit;

// SetupGuardMiddleware._completedCache 是 static 字段，跨测试方法共享；
// 若并行执行 → 一个测试 setup 后另一个测试 IsCompleted 已为 true，破坏 BeforeSetup 类断言。
// 关闭程序集级并行，让 ResetCacheForTest 在 ctor / Dispose 配合下保持每方法干净状态。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
