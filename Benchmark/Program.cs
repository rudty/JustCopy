namespace Benchmark;

using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Exporters;

public class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.CreateEmpty()
            .AddColumnProvider(DefaultColumnProviders.Instance);
        Summary summary = null!;
        _ = summary;

        Console.WriteLine(nameof(MpmcSyncBenchmark));
        summary = BenchmarkRunner.Run<MpmcSyncBenchmark>(config);
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default);

        Console.WriteLine(nameof(AsyncSignalingBenchmark));
        summary = BenchmarkRunner.Run<AsyncSignalingBenchmark>(config);
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default);

        Console.WriteLine(nameof(Mpsc1ThreadAsyncBenchmark));
        summary = BenchmarkRunner.Run<Mpsc1ThreadAsyncBenchmark>(config);
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default);

        Console.WriteLine(nameof(MpscAsyncBenchmark));
        summary = BenchmarkRunner.Run<MpscAsyncBenchmark>(config);
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default);

        Console.WriteLine(nameof(MpscSyncBenchmark));
        summary = BenchmarkRunner.Run<MpscSyncBenchmark>(config);
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default);
    }
}

