using BenchmarkDotNet.Running;

// run with: dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --filter "*"
// for a quick check add: --job dry
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
