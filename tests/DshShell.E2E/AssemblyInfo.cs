using Xunit;

// E2E 测试启动真实 dsh-launcher 进程，而进程间有全局单实例 Mutex（Program.Main）——
// xUnit 默认并行跑多个测试类时会互相抢占互斥量，导致后启动的进程直接退出/窗口不出现
// （CI 偶发"窗口 10s 未出现"根因）。必须串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
