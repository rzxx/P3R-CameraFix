using p3rpc.camfix.Configuration;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace p3rpc.camfix.Template;

public class ModContext
{
    public IModLoader ModLoader { get; set; } = null!;
    public ILogger Logger { get; set; } = null!;
    public Config Configuration { get; set; } = null!;
    public IModConfig ModConfig { get; set; } = null!;
    public IMod Owner { get; set; } = null!;
    public IStartupScanner StartupScanner { get; set; } = null!;
    public IReloadedHooks? Hooks { get; set; }
}
