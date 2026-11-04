using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rechunk;
using Rechunk.Storage.Redis;
using Sample.AzureFunction;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Every instance of the function app talks to the same Redis, so a chunk that one instance has claimed or
        // stored is seen by all the others. The fingerprint store and the dead letter store both live there.
        services
            .AddRechunk(o =>
            {
                o.SimHash.Enabled = true;
                o.MinHash.Enabled = true;

                // Longer than the slowest embedding plus vector write you expect for one message.
                o.Store.LeaseDuration = TimeSpan.FromMinutes(5);

                // Three tries per chunk, then it goes to the dead letter store.
                o.Failures.MaxAttempts = 3;
            })
            .AddRedisStorage(Environment.GetEnvironmentVariable("RechunkRedis") ?? "localhost:6379");

        // These two are stand ins. Replace them with your embedding model and your vector store.
        services.AddSingleton<IChunkEmbedder, DemoEmbedder>();
        services.AddSingleton<IVectorSink, DemoVectorSink>();
    })
    .Build();

host.Run();
