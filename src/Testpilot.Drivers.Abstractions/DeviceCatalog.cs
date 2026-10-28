using System.Globalization;
using System.Text.Json;

namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// One device model that can be part of a test system (for example "HMP4040").
/// <para><see cref="ProfileFile"/>: embedded profile of the device.</para>
/// <para><see cref="ModuleSlotCount"/>: the device is a mainframe; its channels come from the modules in the slots.</para>
/// <para><see cref="ChannelsFromOptions"/>: the channel list is given in the system device options (simulated models).</para>
/// <para><see cref="ConnectionModelOption"/>: real drivers must be configured with this "model" connection option.</para>
/// </summary>
public sealed record DeviceModel(
    string Name,
    string Description,
    DeviceCapability Capability,
    IReadOnlyList<string> Drivers,
    string SimulatedDriver,
    string? ProfileFile = null,
    int ModuleSlotCount = 0,
    IReadOnlyList<string>? Modules = null,
    bool ChannelsFromOptions = false,
    string? ConnectionModelOption = null)
{
    /// <summary>Real drivers plus the simulated driver that may be bound at a station.</summary>
    public IReadOnlyList<string> AllowedDrivers => [.. Drivers, SimulatedDriver];

    public override string ToString() => $"{Name} ({Description})";
}

/// <summary>Options of one device in a test system: modules per slot or a simulated channel list.</summary>
public sealed record SystemTypeDeviceOptions(IReadOnlyDictionary<int, string> Slots, IReadOnlyList<int> Channels)
{
    public static SystemTypeDeviceOptions Empty { get; } = new(new Dictionary<int, string>(), []);

    public string ToJson()
    {
        if (Slots.Count != 0)
            return JsonSerializer.Serialize(new
            {
                slots = Slots.OrderBy(slot => slot.Key)
                    .ToDictionary(slot => slot.Key.ToString(CultureInfo.InvariantCulture), slot => slot.Value)
            });
        return Channels.Count != 0 ? JsonSerializer.Serialize(new { channels = Channels.Order() }) : "{}";
    }
}

/// <summary>
/// All device models that can be assigned to a test system, and the rules for binding a station driver to them.
/// Messages are German because they are shown in the editor and in the operator UI.
/// </summary>
public static class DeviceCatalog
{
    public const string Ag34980A = "34980A";

    public static IReadOnlyList<DeviceModel> Models { get; } =
    [
        new(Ag34980A, "Keysight 34980A Schaltmultimeter mit Modulen", DeviceCapability.SwitchMultimeter,
            ["Ag34980Scpi", "Ag34980Dll"], "SimulatedSwitchMultimeter",
            ModuleSlotCount: 8, Modules: ["34921A", "34937A"]),
        new("HMP2020", "R&S HMP2020 Netzteil", DeviceCapability.PowerSupply, ["HmpScpi", "HmpDll"],
            "SimulatedPowerSupply", ProfileFile: "HMP2020.yaml", ConnectionModelOption: "HMP2020"),
        new("HMP2030", "R&S HMP2030 Netzteil", DeviceCapability.PowerSupply, ["HmpScpi", "HmpDll"],
            "SimulatedPowerSupply", ProfileFile: "HMP2030.yaml", ConnectionModelOption: "HMP2030"),
        new("HMP4030", "R&S HMP4030 Netzteil", DeviceCapability.PowerSupply, ["HmpScpi", "HmpDll"],
            "SimulatedPowerSupply", ProfileFile: "HMP4030.yaml", ConnectionModelOption: "HMP4030"),
        new("HMP4040", "R&S HMP4040 Netzteil", DeviceCapability.PowerSupply, ["HmpScpi", "HmpDll"],
            "SimulatedPowerSupply", ProfileFile: "HMP4040.yaml", ConnectionModelOption: "HMP4040"),
        new("SensoPart", "SensoPart Code-Leser (TCP)", DeviceCapability.BarcodeScanner, ["SensopartTcp"],
            "SimulatedBarcodeScanner", ProfileFile: "CodeReader.yaml"),
        new("Matrix120", "Datalogic Matrix 120 Code-Leser (seriell)", DeviceCapability.BarcodeScanner,
            ["DatalogicMatrixSerial"], "SimulatedBarcodeScanner", ProfileFile: "CodeReader.yaml",
            ConnectionModelOption: "Matrix120"),
        new("Matrix200", "Datalogic Matrix 200 Code-Leser (seriell)", DeviceCapability.BarcodeScanner,
            ["DatalogicMatrixSerial"], "SimulatedBarcodeScanner", ProfileFile: "CodeReader.yaml",
            ConnectionModelOption: "Matrix200"),
        new("PS05-Adapter", "PS05-Testadapter (über 34970A)", DeviceCapability.TestFixture, ["Ps05Fixture"],
            "SimulatedFixture", ProfileFile: "Ps05Fixture.yaml"),
        new("J-Link", "SEGGER J-Link (J-Flash Kommandozeile)", DeviceCapability.Programmer, ["SeggerJFlashCli"],
            "SimulatedProgrammer"),
        new("SimDmm", "Simuliertes Schaltmultimeter", DeviceCapability.SwitchMultimeter, [],
            "SimulatedSwitchMultimeter", ChannelsFromOptions: true),
        new("SimPowerSupply", "Simuliertes Netzteil", DeviceCapability.PowerSupply, [],
            "SimulatedPowerSupply", ChannelsFromOptions: true),
        new("SimProgrammer", "Simulierter Programmer", DeviceCapability.Programmer, [], "SimulatedProgrammer"),
        new("SimCodeReader", "Simulierter Code-Leser", DeviceCapability.BarcodeScanner, [], "SimulatedBarcodeScanner"),
        new("SimFixture", "Simulierter Testadapter", DeviceCapability.TestFixture, [], "SimulatedFixture")
    ];

    public static DeviceModel? Find(string? name) =>
        Models.SingleOrDefault(model => StringComparer.OrdinalIgnoreCase.Equals(model.Name, name));

    public static DeviceModel Get(string? name) => Find(name)
        ?? throw new ArgumentException($"Gerätemodell '{name}' ist unbekannt. Bekannt: {string.Join(", ", Models.Select(model => model.Name))}.");

    /// <summary>
    /// Parses and checks the options of a system device:
    /// <c>{"slots":{"1":"34921A"}}</c> for module mainframes, <c>{"channels":[1,2]}</c> for simulated models, otherwise <c>{}</c>.
    /// </summary>
    public static SystemTypeDeviceOptions ParseOptions(string modelName, string? optionsJson)
    {
        var model = Get(modelName);
        if (string.IsNullOrWhiteSpace(optionsJson))
            optionsJson = "{}";
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Optionen von '{model.Name}' sind kein gültiges JSON: {exception.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Optionen von '{model.Name}' müssen ein JSON-Objekt sein.");

        var slots = new SortedDictionary<int, string>();
        var channels = new List<int>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("slots") && model.ModuleSlotCount > 0)
                ReadSlots(model, property.Value, slots);
            else if (property.NameEquals("channels") && model.ChannelsFromOptions)
                ReadChannels(model, property.Value, channels);
            else
                throw new ArgumentException($"Option '{property.Name}' ist für '{model.Name}' nicht erlaubt.");
        }
        if (model.ModuleSlotCount > 0 && slots.Count == 0)
            throw new ArgumentException($"Beim {model.Name} muss mindestens ein Steckplatz mit einem Modul belegt sein.");
        if (model.ChannelsFromOptions && channels.Count == 0)
            throw new ArgumentException($"Beim simulierten Modell '{model.Name}' muss mindestens ein Kanal angegeben sein.");
        return new SystemTypeDeviceOptions(slots, channels.Order().ToArray());
    }

    /// <summary>The profile the editor and the optimizer use for a device of a test system.</summary>
    public static DeviceProfile CreateProfile(string modelName, string? optionsJson)
    {
        var model = Get(modelName);
        var options = ParseOptions(model.Name, optionsJson);
        DeviceProfile profile;
        if (model.ModuleSlotCount > 0)
            profile = ComposeModules(model, options.Slots);
        else if (model.ProfileFile is not null)
            profile = EmbeddedProfiles.Load(model.ProfileFile);
        else
            profile = SimulatedDeviceProfiles.Create(model.Name.ToLowerInvariant(), model.Description, model.Capability,
                model.ChannelsFromOptions ? options.Channels : null);
        profile.Validate();
        return profile;
    }

    /// <summary>
    /// Checks that a station driver may be bound to a device model of the test system.
    /// Real drivers with a "model" connection option (HMP, Datalogic) must name the same model.
    /// </summary>
    public static void ValidateBinding(string deviceKey, string modelName, string driver, string connectionParametersJson)
    {
        var model = Get(modelName);
        if (!model.AllowedDrivers.Contains(driver, StringComparer.Ordinal))
            throw new ArgumentException(
                $"Gerät '{deviceKey}' ist ein {model.Name}; Treiber '{driver}' passt nicht. Erlaubt: {string.Join(", ", model.AllowedDrivers)}.");
        if (model.ConnectionModelOption is null || driver == model.SimulatedDriver)
            return;
        string? configuredModel = null;
        try
        {
            using var document = JsonDocument.Parse(connectionParametersJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var property in document.RootElement.EnumerateObject())
                    if (StringComparer.OrdinalIgnoreCase.Equals(property.Name, "model") && property.Value.ValueKind == JsonValueKind.String)
                        configuredModel = property.Value.GetString();
        }
        catch (JsonException)
        {
            // Reported below as a missing model; the driver reports the JSON error itself.
        }
        if (!StringComparer.OrdinalIgnoreCase.Equals(configuredModel, model.ConnectionModelOption))
            throw new ArgumentException(
                $"Gerät '{deviceKey}' ist ein {model.Name}, aber die Verbindung ist mit \"model\": \"{configuredModel}\" konfiguriert.");
    }

    /// <summary>Short text for lists, for example "34980A: Slot 1 34921A, Slot 3 34937A".</summary>
    public static string Describe(string modelName, string? optionsJson)
    {
        var model = Find(modelName);
        if (model is null)
            return $"{modelName} (unbekanntes Modell)";
        try
        {
            var options = ParseOptions(model.Name, optionsJson);
            if (options.Slots.Count != 0)
                return $"{model.Name}: {string.Join(", ", options.Slots.Select(slot => $"Slot {slot.Key} {slot.Value}"))}";
            if (options.Channels.Count != 0)
                return $"{model.Name}: Kanäle {string.Join(", ", options.Channels)}";
        }
        catch (ArgumentException)
        {
            return $"{model.Name} (ungültige Optionen)";
        }
        return model.Name;
    }

    private static void ReadSlots(DeviceModel model, JsonElement value, IDictionary<int, string> slots)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"\"slots\" von '{model.Name}' muss ein Objekt wie {{\"1\":\"34921A\"}} sein.");
        foreach (var slot in value.EnumerateObject())
        {
            if (!int.TryParse(slot.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 1 || number > model.ModuleSlotCount)
                throw new ArgumentException($"Steckplatz '{slot.Name}' gibt es beim {model.Name} nicht (1–{model.ModuleSlotCount}).");
            var moduleName = slot.Value.ValueKind == JsonValueKind.String ? slot.Value.GetString() : null;
            var module = model.Modules?.SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item, moduleName))
                ?? throw new ArgumentException(
                    $"Modul '{moduleName}' in Steckplatz {number} ist unbekannt. Bekannt: {string.Join(", ", model.Modules ?? [])}.");
            if (!slots.TryAdd(number, module))
                throw new ArgumentException($"Steckplatz {number} ist doppelt angegeben.");
        }
    }

    private static void ReadChannels(DeviceModel model, JsonElement value, List<int> channels)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"\"channels\" von '{model.Name}' muss eine Liste wie [1, 2] sein.");
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var channel) || channel <= 0)
                throw new ArgumentException($"Kanäle von '{model.Name}' müssen positive ganze Zahlen sein.");
            if (channels.Contains(channel))
                throw new ArgumentException($"Kanal {channel} ist bei '{model.Name}' doppelt angegeben.");
            channels.Add(channel);
        }
        if (channels.Count > 10_000)
            throw new ArgumentException($"'{model.Name}' darf höchstens 10.000 Kanäle haben.");
    }

    /// <summary>
    /// Mainframe profile from the module profiles: module channel n in slot s becomes channel s * 100 + n
    /// (34980A numbering, for example slot 1 channel 3 = 1003). Module TODOs are kept with the slot as prefix.
    /// </summary>
    private static DeviceProfile ComposeModules(DeviceModel model, IReadOnlyDictionary<int, string> slots)
    {
        var channels = new List<ChannelProfile>();
        var specialCases = new List<string>();
        var todos = new List<string>();
        var allCountsKnown = true;
        foreach (var (slot, moduleName) in slots.OrderBy(slot => slot.Key))
        {
            var module = EmbeddedProfiles.Load(moduleName + ".yaml");
            var offset = slot * 100;
            var prefix = $"Slot {slot} ({moduleName})";
            allCountsKnown &= module.ChannelCount.HasValue;
            foreach (var channel in module.Channels)
            {
                if (channel.Number >= 100)
                    throw new InvalidDataException($"Module profile {moduleName} has channel {channel.Number}; only 1..99 fit into slot * 100 + channel.");
                channels.Add(channel with
                {
                    Number = offset + channel.Number,
                    Name = $"{prefix} {channel.Name}",
                    ConflictsWithChannels = channel.ConflictsWithChannels?.Select(number => offset + number).ToHashSet()
                });
            }
            specialCases.AddRange(module.SpecialCases.Select(text => $"{prefix}: {text}"));
            todos.AddRange(module.Todos.Select(text => $"{prefix}: {text}"));
        }
        var defaultState = new ChannelStateProfile(new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), UnknownCommandInvalidatesAll: true);
        var description = string.Join(", ", slots.OrderBy(slot => slot.Key).Select(slot => $"Slot {slot.Key} {slot.Value}"));
        return new DeviceProfile($"{model.Name.ToLowerInvariant()}-system", $"{model.Name} ({description})",
            allCountsKnown ? channels.Count : null, "slot * 100 + channel (for example 1003)",
            new HashSet<DeviceCapability> { model.Capability }, defaultState, channels, specialCases, todos);
    }
}
