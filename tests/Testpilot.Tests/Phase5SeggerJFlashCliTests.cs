using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class Phase5SeggerJFlashCliTests
{
    [Fact]
    public void RegistrationExposesProgrammerCapability()
    {
        var registry = new DriverRegistry().AddSeggerJFlashCli();
        var configuration = CreateConfiguration("C:\\tools\\JFlash.exe", "C:\\tools", ["-open", "{{imagePath}}"]);

        var profile = registry.GetProfile(configuration);

        Assert.Equal(["SeggerJFlashCli"], registry.RegisteredDrivers);
        Assert.Contains(DeviceCapability.Programmer, profile.Capabilities);
        Assert.DoesNotContain(DeviceCapability.SwitchMultimeter, profile.Capabilities);
    }

    [Theory]
    [InlineData("{\"executablePath\":\"tool.exe\",\"arguments\":[\"-open\"]}")]
    [InlineData("{\"executablePath\":\"tool.exe\",\"arguments\":[\"{{imagePath}}\"],\"timeoutSeconds\":0}")]
    [InlineData("{\"executablePath\":\"tool.exe\",\"arguments\":[\"{{imagePath}}\"],\"unknown\":true}")]
    public void InvalidConnectionOptionsAreRejected(string json)
    {
        var registry = new DriverRegistry().AddSeggerJFlashCli();
        var configuration = DriverConfiguration.Parse("programmer", "SeggerJFlashCli", json);

        Assert.Throws<JsonException>(() => registry.GetProfile(configuration));
    }

    [Fact]
    public async Task ConfiguredProcessReceivesImagePathWithoutShellQuoting()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        Assert.False(string.IsNullOrWhiteSpace(commandProcessor));
        var directory = Path.Combine(Path.GetTempPath(), $"testpilot-jflash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var imagePath = Path.Combine(directory, "firmware & test.hex");
            await File.WriteAllTextAsync(imagePath, "test image");
            var registry = new DriverRegistry().AddSeggerJFlashCli();
            var connectionJson = JsonSerializer.Serialize(new
            {
                executablePath = commandProcessor,
                workingDirectory = directory,
                arguments = new[] { "/c", "dir", "{{imagePath}}" },
                timeoutSeconds = 10
            });
            var host = new DriverHost(registry, NullLoggerFactory.Instance);
            await using var lease = await host.AcquireAsync(
                DriverConfiguration.Parse("programmer", "SeggerJFlashCli", connectionJson));

            var programmer = lease.GetCapability<IProgrammer>();
            Assert.Contains("SEGGER J-Flash CLI", await lease.Instrument.IdentifyAsync());
            Assert.True((await lease.Instrument.SelfCheckAsync()).Passed);
            await programmer.ProgramAsync(imagePath);
            await Assert.ThrowsAsync<FileNotFoundException>(() => programmer.ProgramAsync(Path.Combine(directory, "missing.hex")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingExecutableFailsDuringConnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"testpilot-jflash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var registry = new DriverRegistry().AddSeggerJFlashCli();
            var configuration = CreateConfiguration(Path.Combine(directory, "missing.exe"), directory, ["{{imagePath}}"]);
            var host = new DriverHost(registry, NullLoggerFactory.Instance);

            await Assert.ThrowsAsync<FileNotFoundException>(() => host.AcquireAsync(configuration).AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DriverConfiguration CreateConfiguration(string executablePath, string workingDirectory, string[] arguments) =>
        DriverConfiguration.Parse("programmer", "SeggerJFlashCli", JsonSerializer.Serialize(new
        {
            executablePath,
            workingDirectory,
            arguments
        }));
}
