using System.ComponentModel;

namespace Cézanne.Core.Maven
{
    public class MavenConfiguration
    {
        [Category("maven")]
        [Description("Is remote downloading of transitive alveoli enabled.")]
        public bool EnableDownload { get; set; } = false;

        [Category("maven")]
        [Description("HTTP timeout.")]
        public int Timeout { get; set; } = 30_000;

        [Category("maven")]
        [Description("Where to cache maven dependencies. " +
                     "If set to `auto`, tries to read the system property `maven.repo.local`" +
                     " then the `settings.xml` `localRepository`" +
                     " and finally it would fallback on `$HOME/.m2/repository`.")]
        public string LocalRepository { get; set; } = "auto";

        [Category("maven")]
        [Description(
            "If `false` we first try to read `settings.xml` file(s) in `cache` location before the default one.")]
        public bool PreferLocalSettingsXml { get; set; } = true;

        [Category("maven")]
        [Description("If `true` we only use `cache` value and never fallback on default maven settings.xml location.")]
        public bool ForceCustomSettingsXml { get; set; } = false;

        [Category("maven")]
        [Description("Default remote release repository.")]
        public string ReleaseRepository { get; set; } = "https://repo.maven.apache.org/maven2/";

        [Category("maven")]
        [Description("Default remote snapshot repository (if set).")]
        public string? SnapshotRepository { get; set; } = "https://repo.maven.apache.org/maven2/";

        [Category("maven")]
        [Description(
            "Properties to define the headers to set per repository, syntax is `host1=headerName headerValue` " +
            "and it supports as much lines as used repositories. " +
            "Note that you can use maven `~/.m2/settings.xml` servers (potentially ciphered) username/password pairs. " +
            "In this last case the server id must be `bundlebee.<server host>`. " +
            "Still in settings.xml case, if the username is null the password value is used as raw `Authorization` header " +
            "else username/password is encoded as a basic header.")]
        public string? HttpHeaders { get; set; } = "https://repo.maven.apache.org/maven2/";
    }
}