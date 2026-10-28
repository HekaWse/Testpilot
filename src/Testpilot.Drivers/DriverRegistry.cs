using System.Collections.Concurrent;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

public sealed class DriverNotFoundException(string driver)
    : InvalidOperationException($"No instrument driver named '{driver}' is registered.");

public sealed class DriverRegistry
{
    private readonly ConcurrentDictionary<string, Func<DriverConfiguration, IInstrument>> factories = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> RegisteredDrivers => factories.Keys.Order(StringComparer.Ordinal).ToArray();

    public void Register(string driver, Func<DriverConfiguration, IInstrument> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);
        ArgumentNullException.ThrowIfNull(factory);
        if (!factories.TryAdd(driver, factory))
            throw new InvalidOperationException($"Driver '{driver}' is already registered.");
    }

    public DeviceProfile GetProfile(DriverConfiguration configuration) => Create(configuration).Profile;

    internal IInstrument Create(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!factories.TryGetValue(configuration.Driver, out var factory))
            throw new DriverNotFoundException(configuration.Driver);
        var instrument = factory(configuration)
            ?? throw new InvalidOperationException($"Driver factory '{configuration.Driver}' returned null.");
        ArgumentNullException.ThrowIfNull(instrument.Profile);
        instrument.Profile.Validate();
        ValidateCapability(instrument, DeviceCapability.SwitchMultimeter, instrument is ISwitchMultimeter);
        ValidateCapability(instrument, DeviceCapability.PowerSupply, instrument is IPowerSupply);
        ValidateCapability(instrument, DeviceCapability.Programmer, instrument is IProgrammer);
        ValidateCapability(instrument, DeviceCapability.BarcodeScanner, instrument is IBarcodeScanner);
        ValidateCapability(instrument, DeviceCapability.TestFixture, instrument is ITestFixture);
        return instrument;
    }

    private static void ValidateCapability(IInstrument instrument, DeviceCapability capability, bool implemented)
    {
        if (instrument.Profile.Capabilities.Contains(capability) != implemented)
            throw new InvalidOperationException($"Driver profile '{instrument.Profile.Id}' does not match capability interface '{capability}'.");
    }
}
