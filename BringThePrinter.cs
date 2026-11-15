using SonsSdk;
using SonsSdk.Attributes;

namespace BringThePrinter;

public class BringThePrinter : SonsMod, ICustomSaveable<Printers.Data>, IOnInWorldUpdateReceiver, IOnGameActivatedReceiver
{
    public void OnInWorldUpdate()
    {
        Printers.Tick();
    }

    public string Name => "BringThePrinter";
    public bool IncludeInPlayerSave => false;
    public Printers.Data Save() => Printers.Save();
    public void Load(Printers.Data obj) => Printers.Load(obj);

    protected override void OnSdkInitialized()
    {
        SonsSaveTools.Register(this);
        SonsSdk.Networking.Packets.Register(Printers.Net);
        SdkEvents.OnSonsSceneInitialized.Subscribe(s => { if (s == ESonsScene.Title) Printers.Clear(); });
    }

    public BringThePrinter() => HarmonyPatchAll = true;

    public void OnGameActivated() => Printers.Activate();

    protected override void OnGameStart() => Printers.Init();

    [DebugCommand("spawnprinter")]
    static void SpawnPrinter(string args) => Printers.SpawnDebug();

    [DebugCommand("breakprinter")]
    static void BreakPrinter(string args) => Printers.BreakDebug();
}
