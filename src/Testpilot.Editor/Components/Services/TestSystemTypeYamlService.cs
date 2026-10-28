using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Editor.Services;

/// <summary>YAML export and import of a test system type with its device equipment.</summary>
public sealed class TestSystemTypeYamlService
{
    private const int MaximumImportBytes = 1_048_576;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public string Export(TestSystemType system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var document = new SystemDocument
        {
            Name = system.Name,
            Description = system.Description,
            Devices = system.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal)
                .Select(device => new DeviceDocument
                {
                    DeviceKey = device.DeviceKey,
                    Model = device.Model,
                    Description = device.Description,
                    OptionsJson = device.OptionsJson
                }).ToList()
        };
        return Serializer.Serialize(document);
    }

    public TestSystemType Import(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaximumImportBytes)
            throw new InvalidDataException($"Das Prüfsystemtyp-YAML überschreitet {MaximumImportBytes} Bytes.");
        var document = Deserializer.Deserialize<SystemDocument>(yaml)
            ?? throw new InvalidDataException("Das YAML-Dokument muss einen Prüfsystemtyp enthalten.");
        if (string.IsNullOrWhiteSpace(document.Name))
            throw new InvalidDataException("Der Name des Prüfsystemtyps ist Pflicht.");
        if (document.Devices is null)
            throw new InvalidDataException("Die Geräteliste darf nicht null sein.");

        var system = new TestSystemType
        {
            Name = document.Name.Trim(),
            Description = document.Description?.Trim() ?? ""
        };
        foreach (var item in document.Devices)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.DeviceKey))
                throw new InvalidDataException("Die Geräteliste enthält einen Eintrag ohne Gerätekennung.");
            var model = DeviceCatalog.Get(item.Model).Name;
            var options = DeviceCatalog.ParseOptions(model, item.OptionsJson);
            system.Devices.Add(new SystemTypeDevice
            {
                DeviceKey = item.DeviceKey.Trim(),
                Model = model,
                Description = item.Description?.Trim() ?? "",
                OptionsJson = options.ToJson()
            });
        }
        if (system.Devices.Select(device => device.DeviceKey).Distinct(StringComparer.Ordinal).Count() != system.Devices.Count)
            throw new InvalidDataException("Gerätekennungen müssen innerhalb eines Prüfsystemtyps eindeutig sein.");
        return system;
    }

    private sealed class SystemDocument
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; } = "";
        public List<DeviceDocument> Devices { get; set; } = [];
    }

    private sealed class DeviceDocument
    {
        public string DeviceKey { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Description { get; set; } = "";
        public string OptionsJson { get; set; } = "{}";
    }
}
