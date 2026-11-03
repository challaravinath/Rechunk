using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Rechunk.SemanticKernel;

public static class KernelBuilderExtensions
{
    /// <summary>Registers Rechunk in the kernel's service collection.</summary>
    public static IKernelBuilder AddRechunk(this IKernelBuilder builder, Action<RechunkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddRechunk(configure);

        // TODO: check how much is really needed on top of the VectorData package.
        // Semantic Kernel vector stores may already work through it. If so this stays tiny.
        return builder;
    }
}
