using BenchmarkDotNet.Running;
using Rechunk.Benchmarks;

// run with: dotnet run -c Release --project benchmarks/Rechunk.Benchmarks
BenchmarkRunner.Run<ExactHashBenchmarks>(args: args);
