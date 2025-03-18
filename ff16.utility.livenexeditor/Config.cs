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
    public string MonitorPath { get; set; } = "";
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
