using BenchmarkDotNet.Running;
using Rechunk.Benchmarks;

// Benchmarks:  dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --filter "*"
//              add --job dry for a quick check
// Synthetic measurement (a few seconds, not a benchmark):
//              dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --synthetic
if (args.Contains("--synthetic"))
{
    await SyntheticMeasurement.RunAsync(Console.Out);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
