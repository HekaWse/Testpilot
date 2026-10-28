namespace Testpilot.Drivers;

public sealed class NativeDriverScaffoldException(string driver, string libraryPath)
    : NotSupportedException($"Driver '{driver}' is a scaffold only. Native loading and calls are disabled until the library path, process architecture, calling convention, exported signatures, and native memory ownership are verified. Configured library: '{libraryPath}'.")
{
    public string Driver { get; } = driver;
    public string LibraryPath { get; } = libraryPath;
}
