using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Rechunk.SemanticKernel;

namespace Rechunk.Core.Tests;

public class SemanticKernelBuilderTests
{
    [Fact]
    public void AddRechunk_puts_the_pipeline_on_the_kernel_builder()
    {
        var builder = new StubKernelBuilder();

        builder.AddRechunk(options => options.SimHash.Enabled = true);

        using var provider = builder.Services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<RechunkOptions>().SimHash.Enabled);
        Assert.IsType<RechunkPipeline>(provider.GetRequiredService<RechunkPipeline>());
    }

    private sealed class StubKernelBuilder : IKernelBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IKernelBuilderPlugins Plugins => throw new NotSupportedException();
    }
}
