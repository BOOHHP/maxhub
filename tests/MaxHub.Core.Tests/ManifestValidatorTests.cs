using MaxHub.Core.Manifests;

namespace MaxHub.Core.Tests;

public class ManifestValidatorTests
{
    private static ToolManifest Valid(Action<ManifestMutator>? mutate = null)
    {
        var m = new ManifestMutator();
        mutate?.Invoke(m);
        return m.Build();
    }

    [Fact]
    public void Valid_manifest_passes()
    {
        var result = ManifestValidator.Validate(Valid());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("NotReverseDns")]
    [InlineData("com")]
    [InlineData("com..tool")]
    [InlineData("Com.Company.Tool")]
    public void Bad_id_fails(string id) =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Id = id)).IsValid);

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void Bad_version_fails(string version) =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Version = version)).IsValid);

    [Fact]
    public void Wrong_host_type_fails() =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.HostType = "unreal-tapython")).IsValid);

    [Theory]
    [InlineData(2018, 2026)]
    [InlineData(2019, 2027)]
    [InlineData(2024, 2020)]
    public void Bad_year_range_fails(int min, int max) =>
        Assert.False(ManifestValidator.Validate(Valid(m => { m.MinVersion = min; m.MaxVersion = max; })).IsValid);

    [Theory]
    [InlineData("payload/3dsmax/../secrets.txt", "userScripts")]
    [InlineData("/payload/3dsmax/a.ms", "userScripts")]
    [InlineData("payload\\3dsmax\\a.ms", "userScripts")]
    [InlineData("c:/payload/3dsmax/a.ms", "userScripts")]
    [InlineData("other/a.ms", "userScripts")]
    public void Unsafe_or_out_of_payload_source_fails(string source, string destination) =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Targets = [new InstallTarget { Source = source, Destination = destination }])).IsValid);

    [Theory]
    [InlineData("userPlugins")]
    [InlineData("sharedScripts")]
    [InlineData("projectScripts")]
    [InlineData("systemRoot")]
    public void Non_mvp_destination_fails(string destination) =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Targets = [new InstallTarget { Source = "payload/3dsmax/scripts/a.ms", Destination = destination }])).IsValid);

    [Fact]
    public void Machine_scope_fails() =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Scope = "machine")).IsValid);

    [Fact]
    public void Unknown_permission_fails() =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Permissions = ["network.access"])).IsValid);

    [Fact]
    public void Bad_dependency_range_fails() =>
        Assert.False(ManifestValidator.Validate(Valid(m => m.Dependencies = [new DependencySpec { Id = "com.company.lib", Range = "latest" }])).IsValid);

    public sealed class ManifestMutator
    {
        public string Id { get; set; } = "com.company.sample";
        public string Version { get; set; } = "1.0.0";
        public string HostType { get; set; } = "3dsmax";
        public string Scope { get; set; } = "user";
        public int MinVersion { get; set; } = 2019;
        public int MaxVersion { get; set; } = 2026;
        public IReadOnlyList<InstallTarget> Targets { get; set; } =
            [new InstallTarget { Source = "payload/3dsmax/scripts/sample.ms", Destination = "userScripts" }];
        public IReadOnlyList<string>? Permissions { get; set; } = ["file.read"];
        public IReadOnlyList<DependencySpec>? Dependencies { get; set; }

        public ToolManifest Build() => new()
        {
            SchemaVersion = 1,
            Id = Id,
            Name = "Sample",
            Version = Version,
            HostType = HostType,
            Compatibility = new Compatibility { MinVersion = MinVersion, MaxVersion = MaxVersion, Platforms = ["win-x64"] },
            Install = new InstallSpec { Scope = Scope, RestartRequired = false, Targets = Targets },
            Permissions = Permissions,
            Dependencies = Dependencies,
        };
    }
}
