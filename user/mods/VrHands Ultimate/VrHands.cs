using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;

namespace _12Bundle
{
    public record ModMetadata : AbstractModMetadata
    {
        public override string Name { get; init; } = "VRHands Ultimate";
        public override string Author { get; init; } = "Dar'Zhar";
        public override List<string>? Contributors { get; init; }
        public override SemanticVersioning.Version Version { get; init; } = new("1.2.0");
        public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0");
        
        
        public override List<string>? Incompatibilities { get; init; }
        public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
        public override string? Url { get; init; } = "https://forge.sp-tarkov.com/mod/1960/vrhands-ultimate";
        public override bool? IsBundleMod { get; init; } = true;
        public override string? License { get; init; } = "MIT";
        public override string ModGuid { get; init; } = "com.darzhar.vrhandsultimate";
    }

    [Injectable(TypePriority = OnLoadOrder.PostSptModLoader)]
    public class BundleExample(ISptLogger<BundleExample> logger) : IOnLoad
    {
        public Task OnLoad()
        {
            logger.Success("Dar'Zhar's VrHands Ultimate loaded");
            return Task.CompletedTask;
        }
    }
}
