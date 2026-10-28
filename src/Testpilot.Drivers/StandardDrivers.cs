namespace Testpilot.Drivers;

/// <summary>The driver set used by the sequencer hosts (Testpilot.RunPlan and Testpilot.Operator).</summary>
public static class StandardDrivers
{
    /// <summary>Registers the simulated drivers and every physical driver adapter.</summary>
    public static DriverRegistry AddAllDrivers(this DriverRegistry registry) => registry
        .AddSimulatedDrivers()
        .AddSeggerJFlashCli()
        .AddHmpScpi()
        .AddAg34980Scpi()
        .AddHmpDll()
        .AddAg34980Dll()
        .AddVisaGeneric()
        .AddSensopartTcp()
        .AddDatalogicMatrixSerial()
        .AddPs05Fixture();
}
