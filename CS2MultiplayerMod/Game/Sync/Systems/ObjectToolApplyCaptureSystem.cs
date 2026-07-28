using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Observes an object lifecycle tool after it has selected Apply and before ToolOutputSystem
    /// consumes the standing preview. This is the exact hand-off point for one-shot upgrades,
    /// relocations, rebuilds, and rootless asset-stamp transactions.
    /// </summary>
    public partial class ObjectToolApplyCaptureSystem : GameSystemBase
    {
        private BuildSyncSystem _buildSync;

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            Mod.log.Info(nameof(ObjectToolApplyCaptureSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            // World reloads can recreate the synchronization system independently of this hook.
            // Rebind instead of silently losing the one-frame Apply pulse for every later stamp.
            if (_buildSync == null)
                _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _buildSync.CaptureLocalObjectApplyBeforeToolOutput();
        }
    }
}
