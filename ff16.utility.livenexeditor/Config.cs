using ff16.utility.livenexeditor.Template.Configuration;

using Reloaded.Mod.Interfaces.Structs;

using System.ComponentModel;

namespace ff16.utility.livenexeditor.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Monitor NXD file changes")]
    [Description("Whether to monitor file changes")]
    [DefaultValue(false)]
    public bool MonitorNxdChanges { get; set; } = false;

    [DisplayName("Monitor SQL DB changes")]
    [Description("Whether to monitor SQL db changes")]
    [DefaultValue(false)]
    public bool MonitorSqlChanges { get; set; } = false;

    [DisplayName("Folder to monitor for nxd/sqlite files")]
    [Description("Folder to monitor for nxd/sqlite files")]
    [DefaultValue("")]
    [FolderPickerParams(chooseFolderButtonLabel: "Browse")]
    public string MonitorPath { get; set; } = "";

    [DisplayName("Delay in seconds before applying changes")]
    [Description("Wait for the specified amount of seconds before injecting the changes into the game, minimum 0.5")]
    [DefaultValue(4.0f)]
    public float ThrottleSeconds { get; set; } = 4.0f;

    [DisplayName("Attempt editing by index")]
    [Description("Try to edit the data by using the column order instead of name, when the sqlite file was generated using older/newer FF16Tools vesrion, *can break stuff*")]
    [DefaultValue(false)]
    public bool AttemptEditByIndex { get; set; } = false;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}