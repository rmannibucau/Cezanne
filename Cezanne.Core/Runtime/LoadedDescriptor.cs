using Cezanne.Core.Descriptor;

namespace Cezanne.Core.Runtime
{
    public record LoadedDescriptor(
        Manifest.Descriptor Configuration,
        string Content,
        string Extension,
        string Uri,
        string Resource
    ) { }
}
