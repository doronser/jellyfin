using System;
using System.IO;
using Jellyfin.Server.Migrations.Routines;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class CreateUserLoggingConfigFileTests : IDisposable
{
    // Minified form of a historical default logging.json (65fe243afbcc4b596cf8726708c1965cd34b5f68 in the migration).
    private const string HistoricalConfig = """{"Serilog":{"MinimumLevel":"Information","WriteTo":[{"Name":"Console","Args":{"outputTemplate":"[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"}},{"Name":"Async","Args":{"configure":[{"Name":"File","Args":{"path":"%JELLYFIN_LOG_DIR%//log_.log","rollingInterval":"Day","retainedFileCountLimit":3,"rollOnFileSizeLimit":true,"fileSizeLimitBytes":100000000,"outputTemplate":"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message}{NewLine}{Exception}"}}]}}]}}""";

    private readonly string _configDirectory;
    private readonly string _loggingPath;
    private readonly string _oldLoggingPath;
    private readonly CreateUserLoggingConfigFile _migration;

    public CreateUserLoggingConfigFileTests()
    {
        _configDirectory = Directory.CreateTempSubdirectory("jellyfin-logging-migration-").FullName;
        _loggingPath = Path.Combine(_configDirectory, "logging.json");
        _oldLoggingPath = Path.Combine(_configDirectory, "logging.old.json");

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(_configDirectory);
        _migration = new CreateUserLoggingConfigFile(paths.Object);
    }

    public void Dispose() => Directory.Delete(_configDirectory, true);

    [Fact]
    public void Perform_NoExistingFile_DoesNothing()
    {
        _migration.Perform();

        Assert.False(File.Exists(_loggingPath));
        Assert.False(File.Exists(_oldLoggingPath));
    }

    [Fact]
    public void Perform_UnmodifiedConfig_MovesToOld()
    {
        File.WriteAllText(_loggingPath, HistoricalConfig);

        _migration.Perform();

        Assert.False(File.Exists(_loggingPath));
        Assert.True(File.Exists(_oldLoggingPath));
    }

    [Fact]
    public void Perform_UnmodifiedConfigWithDifferentFormatting_MovesToOld()
    {
        File.WriteAllText(_loggingPath, "{\n  \"Serilog\": " + HistoricalConfig[11..^1] + "\n}\n");

        _migration.Perform();

        Assert.False(File.Exists(_loggingPath));
        Assert.True(File.Exists(_oldLoggingPath));
    }

    [Fact]
    public void Perform_UnmodifiedConfigWithReorderedProperties_MovesToOld()
    {
        const string Config = """{"Serilog":{"WriteTo":[{"Name":"Console","Args":{"outputTemplate":"[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"}},{"Name":"Async","Args":{"configure":[{"Args":{"path":"%JELLYFIN_LOG_DIR%//log_.log","rollingInterval":"Day","retainedFileCountLimit":3,"rollOnFileSizeLimit":true,"fileSizeLimitBytes":100000000,"outputTemplate":"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message}{NewLine}{Exception}"},"Name":"File"}]}}],"MinimumLevel":"Information"}}""";
        File.WriteAllText(_loggingPath, Config);

        _migration.Perform();

        Assert.False(File.Exists(_loggingPath));
        Assert.True(File.Exists(_oldLoggingPath));
    }

    [Fact]
    public void Perform_ModifiedConfig_IsKept()
    {
        File.WriteAllText(_loggingPath, HistoricalConfig.Replace("\"Information\"", "\"Debug\"", StringComparison.Ordinal));

        _migration.Perform();

        Assert.True(File.Exists(_loggingPath));
        Assert.False(File.Exists(_oldLoggingPath));
    }

    [Fact]
    public void Perform_ConfigWithCommentsAndTrailingCommas_IsParsedLeniently()
    {
        File.WriteAllText(_loggingPath, "// user comment\n{\"Serilog\":{\"MinimumLevel\":\"Debug\",},}");

        _migration.Perform();

        Assert.True(File.Exists(_loggingPath));
        Assert.False(File.Exists(_oldLoggingPath));
    }
}
