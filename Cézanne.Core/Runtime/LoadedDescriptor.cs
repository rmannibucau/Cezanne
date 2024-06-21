using Cézanne.Core.Descriptor;

namespace Cézanne.Core.Runtime
{
    public record LoadedDescriptor(
        Manifest.Descriptor Configuration,
        string Content,
        string Extension,
        string Uri,
        string Resource
    ) { }
}
