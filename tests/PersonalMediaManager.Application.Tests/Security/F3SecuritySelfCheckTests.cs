using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.Security;

/// <summary>
/// F.3 安全自检（dev plan §F.3）— 可自动化部分
/// - F.3.1 路径穿越（PathSafetyGuard）已有 PathSafetyGuardTests 覆盖；此处补 1 条 zip-style 命名混合用例
/// - F.3.3 正则 ReDoS：恶意 `(a+)+` + 长输入 → 500ms 内中断（不真死循环）
/// - F.3.4 密钥不入日志：源码扫描守卫，禁止 Logxxx 行带 ApiKey / Password / SecretKey / SigningKey 占位符
///
/// F.3.2 Zip Slip 已由 SystemControllerTests.Import_RejectsZipSlip 在 Host.Tests 覆盖
/// </summary>
public sealed class F3SecuritySelfCheckTests
{
    // ====== F.3.1 路径穿越加强用例 ======

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../Evil/x.mkv")]
    [InlineData("..\\..\\windows\\system32\\evil.exe")]
    public void F3_1_PathTraversal_AllVariantsRejected(string evilSegment)
    {
        string root = OperatingSystem.IsWindows() ? @"C:\Media\Movies" : "/Media/Movies";
        string target = Path.Combine(root, evilSegment);
        Action act = () => PathSafetyGuard.EnsureWithinRoot(root, target);
        act.Should().Throw<PathTraversalException>("F.3.1 路径穿越构造应被拒绝");
    }

    // ====== F.3.3 ReDoS 守卫 ======

    [Fact(DisplayName = "F.3.3 恶意嵌套量词正则 (a+)+ 在 500ms 内必须中断而不是死循环")]
    public void F3_3_ReDoS_MalignantPattern_TimesOutWithin500ms()
    {
        // 经典的 ReDoS 模式：(a+)+ 对 'aaaa...!' 类输入会爆炸性回溯
        const string evilPattern = @"^(a+)+$";
        string longInput = new string('a', 30) + "!"; // 30 个 a 后跟不匹配字符，触发完整回溯

        Regex re = new(evilPattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            _ = re.Match(longInput);
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
        }
        sw.Stop();

        timedOut.Should().BeTrue("(a+)+ 对失配长输入必触发 ReDoS，应被 500ms 超时拦截");
        sw.ElapsedMilliseconds.Should().BeLessThan(1500, "超时上限 500ms，留 1000ms 余量给 RegexMatchTimeoutException 抛出 + 测试运行环境抖动");
    }

    [Fact(DisplayName = "F.3.3 正常正则在超时窗口内必须可正常完成")]
    public void F3_3_NormalRegex_CompletesWithinTimeout()
    {
        Regex re = new(@"^(?<year>\d{4})$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
        Match m = re.Match("2010");
        m.Success.Should().BeTrue();
        m.Groups["year"].Value.Should().Be("2010");
    }

    // ====== F.3.4 密钥不入日志（源码守卫） ======

    [Fact(DisplayName = "F.3.4 src/ 下任何 LogXxx 行都不得带未脱敏的密钥占位符（ApiKey / Password / SigningKey）")]
    public void F3_4_NoSecretLeakInLoggingCallSites()
    {
        // 定位 src/ 目录（测试运行时位于 tests/.../bin/Debug/net10.0/，向上找 src）
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("应在测试运行目录祖先中找到仓库根");
        string srcDir = Path.Combine(dir!.FullName, "src");

        // 仅扫 *.cs 排除 bin/obj
        IEnumerable<string> files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

        // 禁止 LogXxx(..., 含敏感占位 + 同行同语句拼接) 例如 _logger.LogInformation("token={Token}", token);
        // 关键字（大小写敏感，避免误伤 nameOfFn 包含 password 的合理用法）
        string[] forbiddenTokens = new[]
        {
            "{ApiKey}", "{apiKey}",
            "{Password}", "{password}",
            "{SigningKey}", "{signingKey}",
            "{Secret}", "{secret}",
            "{Token}", "{token}",
            "{BearerToken}", "{bearerToken}",
        };
        // Log* 调用前缀（覆盖 _logger.LogInformation / logger.LogError 等）
        Regex logCallPattern = new(@"\bLog(Information|Warning|Error|Debug|Trace|Critical)\b", RegexOptions.Compiled);

        List<string> violations = new();
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!logCallPattern.IsMatch(line)) continue;
                foreach (string forbidden in forbiddenTokens)
                {
                    if (line.Contains(forbidden, StringComparison.Ordinal))
                    {
                        string rel = Path.GetRelativePath(srcDir, file);
                        violations.Add($"{rel}:{i + 1}  -> 命中禁用占位 {forbidden}");
                    }
                }
            }
        }

        violations.Should().BeEmpty("源码层任何 LogXxx 都不应携带未脱敏的密钥占位符（CLAUDE.md §F.3.4）");
    }
}
