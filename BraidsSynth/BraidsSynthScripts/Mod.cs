using Modding;
using Modding.Modules;
using UnityEngine;

namespace BraidsSynth
{
    /// <summary>
    /// Entry point. Registers the synth block, so a &lt;BraidsSynth&gt; element in
    /// the block XML is driven by <see cref="BraidsBehaviour"/>, and puts up the object
    /// that watches for the block mapper opening on one.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        private const string HostName = "BraidsPanelHost";

        private static GameObject host;

        public override void OnLoad()
        {
            CustomModules.AddBlockModule<SynthModule, BraidsBehaviour>("BraidsSynth", false);

            if (host != null)
            {
                return;
            }
            // Outlives scene loads: the mapper's callbacks are plain static
            // delegates, so whatever subscribes to them has to be around for as
            // long as they are.
            host = new GameObject(HostName);
            Object.DontDestroyOnLoad(host);
            host.AddComponent<BraidsPanel>();

            Log.Info("loaded. The Synth Block is in the toolbar; clicking one opens its "
                     + "panel, where the model can be picked and heard.");
        }
    }
}
