using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Editor.Services;

/// <summary>YAML export and import of a concrete test system (station): type name, devices, drivers and addresses.</summary>
public sealed class StationYamlService
{
    private const int MaximumImportBytes = 1_048_576;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <param name="systemTypeName">Name of the test system type of the station, if any.</param>
    public string Export(Station station, string? systemTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        var document = new StationDocument
        {
            Name = station.Name,
            SystemType = systemTypeName,
            Description = string.IsNullOrWhiteSpace(station.Description) ? null : station.Description,
            Devices = station.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal)
                .Select(device => new DeviceDocument
                {
                    DeviceKey = device.DeviceKey,
                    Driver = device.Driver,
                    ConnectionParametersJson = device.ConnectionParametersJson
                }).ToList()
        };
        return Serializer.Serialize(document);
    }

    public Station Import(string yaml) => Import(yaml, []);

    /// <summary>
    /// Imports a station. A named test system type must be one of <paramref name="types"/>; every device must then
    /// belong to that type and its driver must fit the device model.
    /// </summary>
    public Station Import(string yaml, IReadOnlyCollection<TestSystemType> types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        ArgumentNullException.ThrowIfNull(types);
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaximumImportBytes)
            throw new InvalidDataException($"Das Prüfsystem-YAML überschreitet {MaximumImportBytes} Bytes.");
        var document = Deserializer.Deserialize<StationDocument>(yaml)
            ?? throw new InvalidDataException("Das YAML-Dokument muss ein Prüfsystem enthalten.");
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Name);
        if (document.Devices is null)
            throw new InvalidDataException("Die Geräteliste darf nicht null sein.");

        var station = new Station { Name = document.Name.Trim(), Description = document.Description?.Trim() ?? "" };
        TestSystemType? type = null;
        if (!string.IsNullOrWhiteSpace(document.SystemType))
        {
            type = types.SingleOrDefault(item => item.Name == document.SystemType.Trim())
                ?? throw new InvalidDataException($"Prüfsystemtyp '{document.SystemType}' gibt es nicht. Bitte zuerst den Prüfsystemtyp anlegen oder importieren.");
            station.TestSystemTypeId = type.Id;
        }
        foreach (var item in document.Devices)
        {
            if (item is null)
                throw new InvalidDataException("Die Geräteliste enthält einen ungültigen Eintrag.");
            ArgumentException.ThrowIfNullOrWhiteSpace(item.DeviceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Driver);
            using var parameters = System.Text.Json.JsonDocument.Parse(item.ConnectionParametersJson);
            if (parameters.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidDataException("Verbindungsparameter müssen ein JSON-Objekt sein.");
            if (type is not null)
            {
                var typeDevice = type.Devices.SingleOrDefault(device => device.DeviceKey == item.DeviceKey.Trim())
                    ?? throw new InvalidDataException($"Der Prüfsystemtyp '{type.Name}' enthält kein Gerät '{item.DeviceKey}'.");
                DeviceCatalog.ValidateBinding(typeDevice.DeviceKey, typeDevice.Model, item.Driver.Trim(), item.ConnectionParametersJson);
            }
            station.Devices.Add(new StationDevice
            {
                DeviceKey = item.DeviceKey.Trim(),
                Driver = item.Driver.Trim(),
                ConnectionParametersJson = item.ConnectionParametersJson
            });
        }
        if (station.Devices.Select(device => device.DeviceKey).Distinct(StringComparer.Ordinal).Count() != station.Devices.Count)
            throw new InvalidDataException("Gerätekennungen müssen innerhalb eines Prüfsystems eindeutig sein.");
        return station;
    }

    private sealed class StationDocument
    {
        public string Name { get; set; } = "";
        public string? SystemType { get; set; }
        public string? Description { get; set; }
        public List<DeviceDocument> Devices { get; set; } = [];
    }

    private sealed class DeviceDocument
    {
        public string DeviceKey { get; set; } = "";
        public string Driver { get; set; } = "";
        public string ConnectionParametersJson { get; set; } = "{}";
    }
}
