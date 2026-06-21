using p3rpc.camfix.Configuration;
using Reloaded.Mod.Interfaces;

namespace p3rpc.camfix.Template.Configuration;

public class Configurator : IConfiguratorV3
{
    private static ConfiguratorMixin _configuratorMixin = new();

    public string? ModFolder { get; private set; }
    public string? ConfigFolder { get; private set; }
    public ConfiguratorContext Context { get; private set; }

    public IUpdatableConfigurable[] Configurations => _configurations ?? MakeConfigurations();
    private IUpdatableConfigurable[]? _configurations;

    private IUpdatableConfigurable[] MakeConfigurations()
    {
        _configurations = _configuratorMixin.MakeConfigurations(ConfigFolder!);
        for (int x = 0; x < Configurations.Length; x++)
        {
            var xCopy = x;
            Configurations[x].ConfigurationUpdated += configurable =>
            {
                Configurations[xCopy] = configurable;
            };
        }
        return _configurations;
    }

    public Configurator() { }
    public Configurator(string configDirectory) : this() => ConfigFolder = configDirectory;

    public void Migrate(string oldDirectory, string newDirectory) => _configuratorMixin.Migrate(oldDirectory, newDirectory);

    public TType GetConfiguration<TType>(int index) => (TType)Configurations[index];

    public void SetConfigDirectory(string configDirectory) => ConfigFolder = configDirectory;
    public void SetContext(in ConfiguratorContext context) => Context = context;
    public IConfigurable[] GetConfigurations() => Configurations;
    public bool TryRunCustomConfiguration() => _configuratorMixin.TryRunCustomConfiguration(this);
    public void SetModDirectory(string modDirectory) { ModFolder = modDirectory; }
}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}

public class ConfiguratorMixinBase
{
    public virtual IUpdatableConfigurable[] MakeConfigurations(string configFolder)
    {
        return new IUpdatableConfigurable[]
        {
            Configurable<Config>.FromFile(Path.Combine(configFolder, "Config.json"), "Default Config")
        };
    }

    public virtual bool TryRunCustomConfiguration(Configurator configurator) => false;

    public virtual void Migrate(string oldDirectory, string newDirectory) { }
}
