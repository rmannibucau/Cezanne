using System.ComponentModel;
using Cezanne.Core.Lang;

namespace Cezanne.Core.Maven
{
    [ConfigurationPrefix("nuget")]
    [Description("Configuration of the NuGet local cache and remote repository(ies).")]
    public class NuGetConfiguration
    {
        [Description("Is remote downloading of transitive recipes enabled.")]
        public bool EnableDownload { get; set; } = false;

        [Description("HTTP timeout.")]
        public int Timeout { get; set; } = 30_000;

        [Description("Where to lookup NuGet packages locally before going remotely.")]
        public string LocalRepository { get; set; } = "auto";

        [Description(
            "Default remote release repository, it is the seed `index.json` URL - flat container url being dediced from this one."
        )]
        public string Repository { get; set; } = "https://api.nuget.org/v3/index.json";

        [Description(
            "Properties to define the headers to set per repository, syntax is `host1=headerName headerValue` "
                + "and it supports as much lines as used repositories. "
                + "Generally used to set API token if needed."
        )]
        public string? HttpHeaders { get; set; }
    }
}
