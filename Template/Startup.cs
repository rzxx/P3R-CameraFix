using p3rpc.camfix.Configuration;
using p3rpc.camfix.Template.Configuration;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace p3rpc.camfix.Template;

public class Startup : IMod
{
    private ILogger _logger = null!;
    private IModLoader _modLoader = null!;
    private Config _configuration = null!;
    private IModConfig _modConfig = null!;
    private IReloadedHooks? _hooks;
    private ModBase _mod = new Mod();

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        _modLoader = (IModLoader)loaderApi;
        _modConfig = (IModConfig)modConfig;
        _logger = (ILogger)_modLoader.GetLogger();
        _modLoader.GetController<IReloadedHooks>()?.TryGetTarget(out _hooks!);

        var configurator = new Configurator(_modLoader.GetModConfigDirectory(_modConfig.ModId));
        _configuration = configurator.GetConfiguration<Config>(0);
        _configuration.ConfigurationUpdated += OnConfigurationUpdated;

        var startupScannerController = _modLoader.GetController<IStartupScanner>();
        IStartupScanner? startupScanner = null;
        startupScannerController?.TryGetTarget(out startupScanner);

        _mod = new Mod(new ModContext()
        {
            Logger = _logger,
            ModLoader = _modLoader,
            ModConfig = _modConfig,
            Owner = this,
            Configuration = _configuration,
            StartupScanner = startupScanner!,
            Hooks = _hooks,
        });
    }

    private void OnConfigurationUpdated(IConfigurable obj)
    {
        _configuration = (Config)obj;
        _mod.ConfigurationUpdated(_configuration);
    }

    public void Suspend() => _mod.Suspend();
    public void Resume() => _mod.Resume();
    public void Unload() => _mod.Unload();
    public bool CanUnload() => _mod.CanUnload();
    public bool CanSuspend() => _mod.CanSuspend();
    public Action Disposing => () => _mod.Disposing();
}
