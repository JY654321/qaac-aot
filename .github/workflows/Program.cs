using System.Diagnostics;
using System.Text;

namespace QaacBatch;

internal static class Program
{
    // 定死项
    private const string EncodeQuality = "2";     // -q 2
    private const string DefaultVbr   = "82";     // V82

    private static async Task<int> Main(string[] args)
    {
        // 控制台编码（交互模式下的显示用；路径本身走 args 不受影响）
        try { Console.InputEncoding  = Encoding.UTF8; } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Console.WriteLine("========================================");
        Console.WriteLine("  qaac 批量 FLAC → AAC 转换工具");
        Console.WriteLine("========================================");
        Console.WriteLine();

        string exeDir = AppContext.BaseDirectory
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // ---------- 1. 输入路径（命令行参数优先，可多个） ----------
        var inputs = new List<string>();
        if (args.Length > 0)
        {
            inputs.AddRange(args);
            Console.WriteLine($"1) 输入: {string.Join(" | ", inputs)}   ← 命令行参数");
        }
        else
        {
            Console.Write($"1) 输入文件/文件夹 [{exeDir}]: ");
            string s = ReadLine();
            inputs.Add(s.Length == 0 ? exeDir : s);
        }

        // ---------- 2. 输出目录（留空则按源位置决定 m4a 子目录） ----------
        Console.Write("2) 输出目录 [Enter 默认: 源旁边的 m4a 子目录]: ");
        string outputInput = ReadLine();

        // ---------- 3. qaac.exe 路径 ----------
        Console.Write("3) qaac.exe 路径 [PATH 自动查找]: ");
        string qaacPath = ReadLine();
        if (qaacPath.Length == 0)
        {
            qaacPath = FindInPath("qaac.exe") ?? "qaac.exe";
            Console.WriteLine($"   → 使用: {qaacPath}");
        }

        // ---------- 4. 编码级别 ----------
        Console.Write("4) 编码级别 [1]V82  [2]V91  [3]V100 (默认1): ");
        string vbr = ReadLine() switch
        {
            "2" => "91",
            "3" => "100",
            _   => DefaultVbr,
        };

        // ---------- 5. 进程数 ----------
        int cpuCount = Environment.ProcessorCount;
        Console.Write($"5) 进程数 4/6/8/12/16 (默认4, 逻辑核心 {cpuCount}): ");
        string workerStr = ReadLine();
        int workers = int.TryParse(workerStr, out int w) && w > 0 ? w : 4;

        if (workers > cpuCount)
        {
            Console.WriteLine($"   ⚠️ 选择 {workers} > 核心数 {cpuCount}，已调整为 {cpuCount}");
            workers = cpuCount;
        }

        // ---------- 6. 编码类型 ----------
        Console.Write("6) 编码类型 [1]m4a  [2]AAC裸流 (默认1): ");
        bool rawAac = ReadLine() == "2";
        string ext  = rawAac ? ".aac" : ".m4a";

        // ---------- 收集文件（遍历所有输入源） ----------
        var files = new List<string>();
        bool isSingleFile = false;

        foreach (var one in inputs)
        {
            if (!TryCollect(one, out var fs, out string? err, out bool single))
            {
                Console.WriteLine($"❌ {err}");
                Console.WriteLine("按 Enter 退出...");
                Console.ReadLine();
                return 1;
            }
            files.AddRange(fs);
            isSingleFile |= single;
        }

        // 去重（同一个文件被拖两次也不重复编）
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("⚠️ 未找到 FLAC 文件。");
            Console.WriteLine("按 Enter 退出...");
            Console.ReadLine();
            return 1;
        }

        // ---------- 计算输出目录 ----------
        string outputDir;
        if (outputInput.Length > 0)
        {
            outputDir = outputInput;
        }
        else
        {
            string baseInput = Path.GetFullPath(inputs[0]);
            outputDir = isSingleFile && inputs.Count == 1
                ? Path.Combine(Path.GetDirectoryName(baseInput)!, "m4a")
                : Directory.Exists(baseInput)
                    ? Path.Combine(baseInput, "m4a")
                    : Path.Combine(Path.GetDirectoryName(baseInput)!, "m4a");
        }

        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"   → 输出目录: {outputDir}");

        Console.WriteLine();
        Console.WriteLine($"共 {files.Count} 个文件，使用 {workers} 进程。");
        Console.WriteLine("按 Enter 开始转换...");
        Console.ReadLine();
        Console.WriteLine();

        // ---------- 并行转换 ----------
        int total = files.Count;
        int ok = 0, fail = 0, idx = 0;
        object gate = new();

        var parOpts = new ParallelOptions { MaxDegreeOfParallelism = workers };

        await Parallel.ForEachAsync(files, parOpts, async (file, ct) =>
        {
            string outFile = Path.Combine(
                outputDir,
                Path.GetFileNameWithoutExtension(file) + ext);

            var (success, log) = await RunQaacAsync(qaacPath, file, outFile, vbr, rawAac);

            int n = Interlocked.Increment(ref idx);
            if (success) Interlocked.Increment(ref ok);
            else         Interlocked.Increment(ref fail);

            lock (gate)
            {
                if (success)
                {
                    Console.WriteLine($"[{n}/{total}] ✅ {Path.GetFileName(file)}");
                }
                else
                {
                    Console.WriteLine($"[{n}/{total}] ❌ {Path.GetFileName(file)}");
                    if (!string.IsNullOrWhiteSpace(log))
                    {
                        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            Console.WriteLine("     " + line.TrimEnd());
                    }
                }
            }
        });

        Console.WriteLine();
        Console.WriteLine($"🎉 完成：成功 {ok}，失败 {fail}");
        Console.WriteLine($"📁 输出目录: {outputDir}");
        Console.WriteLine("按 Enter 退出...");
        Console.ReadLine();
        return 0;
    }

    // ==================== 辅助 ====================

    private static string ReadLine() =>
        (Console.ReadLine() ?? string.Empty).Trim().Trim('"');

    private static bool TryCollect(
        string input,
        out List<string> files,
        out string? error,
        out bool isSingleFile)
    {
        files = new List<string>();
        error = null;
        isSingleFile = false;

        try
        {
            if (File.Exists(input))
            {
                isSingleFile = true;
                if (input.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(Path.GetFullPath(input));
                    return true;
                }
                error = $"不是 FLAC 文件: {input}";
                return false;
            }

            if (Directory.Exists(input))
            {
                files.AddRange(
                    Directory.EnumerateFiles(input, "*.flac", SearchOption.TopDirectoryOnly));
                files = files.OrderBy(File.GetLastWriteTime).ToList();
                return true;
            }

            error = $"路径不存在: {input}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? FindInPath(string name)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法路径段 */ }
        }
        return null;
    }

    private static async Task<(bool ok, string log)> RunQaacAsync(
        string qaacPath, string input, string output, string vbr, bool rawAac)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = qaacPath,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        // 定死参数
        psi.ArgumentList.Add("-V");              // VBR
        psi.ArgumentList.Add(vbr);
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(EncodeQuality);     // -q 2

        if (rawAac)
            psi.ArgumentList.Add("--adts");      // 裸流 = ADTS（无容器，不能加封面）
        else
            psi.ArgumentList.Add("--copy-artwork"); // m4a 才复制封面与元数据

        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);

        var sb = new StringBuilder();
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();

            return (p.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}