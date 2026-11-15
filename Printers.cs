using System.Collections;
using Bolt;
using HarmonyLib;
using RedLoader;
using Sons.Crafting.Structures;
using Sons.Electricity;
using Sons.Items.Core;
using Sons.Weapon;
using SonsSdk;
using SonsSdk.Networking;
using TheForest.Utils;
using UdpKit;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BringThePrinter;

public static class Printers
{
    public const int PartsId = 7390;
    public const int LaptopId = 7391;
    public const int PrinterEntity = 7390741, LaptopEntity = 7390742, PrinterDevice = 7390743, LaptopDevice = 7390744, PartsPickup = 7390745, LaptopPickup = 7390746;
    const float Reach = 2.5f;
    const float RespawnDistance = 60f;
    const float BreakAmount = 36f;
    const float PulseLength = 1.5f;
    static readonly Dictionary<string, string> Sounds = new()
    {
        ["PrinterOn"] = "event:/SotF Events/player sounds/ElectricGrid/Electrical_Pulse",
        ["LaptopOn"] = "event:/SotF Events/player sounds/ElectricGrid/Electric_LightSwitch",
        ["PrinterOff"] = "event:/endgame/sfx_endgame/button_click_off",
        ["LaptopOff"] = "event:/endgame/sfx_endgame/button_click_off",
        ["PrinterHit"] = "event:/player/actions/impacts/metal_plane_impact",
        ["LaptopHit"] = "event:/physics/metal/metal_impact",
        ["PrinterBreak"] = "event:/SotF Events/InGame Sequences/Gold Room/Goldroom_2056_LaptopSmash",
        ["LaptopBreak"] = "event:/physics/Devices/break_laptop",
        ["Sparks"] = "event:/SotF Events/Ambient/Spark",
    };
    static readonly Vector3 PartsSlot = new(-2.45f, 0, 1.75f), LaptopSlot = new(-2.45f, 0, 2.3f);

    public class Placed
    {
        public int Recipe = Blueprint.PrinterRecipeId;
        public float X, Y, Z, Yaw, Resin, Time;
        public bool Printing, Done;
        public int Job;
    }

    public class Drop
    {
        public int Id;
        public float X, Y, Z;
    }

    public class Data
    {
        public List<Placed> Printers = new();
        public List<Drop> Drops = new();
        public List<Blueprint.Pending> Blueprints = new();
    }

    public class Station
    {
        public Resin3dPrinter Printer;
        public Resin3dPrinterLaptop Laptop;
    }

    public class Built
    {
        public ElectricDeviceScrewStructure Structure;
        public PowerFlowIndicator Power;
        public Resin3dPrinter Printer;
        public Resin3dPrinterLaptop Laptop;
        public Placed Placed;
        public bool? On, Held;
        public Component Device => Printer ? Printer : Laptop;
        public bool IsPrinter => Placed.Recipe == Blueprint.PrinterRecipeId;
        public bool Powered => Power._hasPower && !Power._isOverloaded;
        public Wire Wire => Structure.GetComponent<Wire>();
    }

    public class Sync : Packets.NetEvent
    {
        public override string Id => "BringThePrinter_Stations";

        public void Request(int station, bool laptop)
        {
            var p = NewPacket(8, GlobalTargets.OnlyServer);
            p.Packet.WriteInt(station);
            p.Packet.WriteBool(laptop);
            Send(p);
        }

        public void Broadcast(int mask)
        {
            var p = NewPacket(4, GlobalTargets.Everyone);
            p.Packet.WriteInt(mask);
            Send(p);
        }

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (BoltNetwork.isServer) Requested(packet.ReadInt(), packet.ReadBool());
            else Apply(packet.ReadInt());
        }
    }

    public static Sync Net { get; } = new();
    public static Data State { get; private set; } = new();
    public static List<Station> Stations { get; } = new();
    public static List<Built> Rebuilt { get; } = new();
    public static List<(int Id, GameObject Go)> Drops { get; } = new();
    static readonly List<UnityEngine.Events.UnityEventBase> Pinned = new();
    static Station _template;
    static GameObject _printerPrefab, _laptopPrefab;
    static Transform _holder;
    static float _nextTick, _nextScan;

    public static Transform Holder
    {
        get
        {
            if (_holder) return _holder;
            var go = new GameObject("BringThePrinterPrefabs");
            go.SetActive(false);
            return _holder = go.DontDestroyOnLoad().HideAndDontSave().transform;
        }
    }

    public static void Clear()
    {
        State = new Data();
        Stations.Clear();
        Rebuilt.Clear();
        Drops.Clear();
        _template = null;
    }

    public static Data Save()
    {
        Prune();
        foreach (var b in Rebuilt.Where(b => b.Printer)) Store(b);
        Drops.RemoveAll(d => !d.Go || !d.Go.activeInHierarchy);
        State.Blueprints = Blueprint.Save();
        State.Drops = Drops.Select(d => new Drop { Id = d.Id, X = d.Go.transform.position.x, Y = d.Go.transform.position.y, Z = d.Go.transform.position.z }).ToList();
        return State;
    }

    static void Store(Built b)
    {
        var (p, printer) = (b.Placed, b.Printer);
        (p.Resin, p.Time, p.Printing, p.Done, p.Job) = (printer.CurrentResinVolume, printer._timeInCurrentPrintJob, printer.IsPrinting, printer.HasCompletedPrintsOnTheBed, printer._printedItemBlueprint ? printer._printedItemBlueprint.Id : -1);
    }

    static void Restore(Built b)
    {
        var (p, printer) = (b.Placed, b.Printer);
        printer._currentResinVolume = p.Resin;
        var job = p.Printing || p.Done ? Resources.FindObjectsOfTypeAll<PrintedItemBlueprint>().FirstOrDefault(x => x.Id == p.Job) : null;
        if (job)
        {
            printer.LoadBlueprint(job);
            printer.StartPrint(p.Done ? job.TotalPrintTime : p.Time);
        }
        if (BoltNetwork.isServer) printer.RefillResin(0, true);
    }

    public static void Load(Data data)
    {
        State = data ?? new Data();
        State.Printers ??= new();
        State.Drops ??= new();
        State.Blueprints ??= new();
        if (_template != null && !BoltNetwork.isClient) SpawnAll();
    }

    public static void Activate()
    {
        if (_template != null) return;
        var bunker = Resources.FindObjectsOfTypeAll<Resin3dPrinter>()
            .Where(p => p.gameObject.scene.IsValid() && p._connectedLaptop && p.SerializedName.StartsWith("Resin3dPrinter_"))
            .OrderBy(p => p.SerializedName)
            .Select(p => new Station { Printer = p, Laptop = p._connectedLaptop }).ToList();
        if (bunker.Count == 0) return;

        Stations.Clear();
        Stations.AddRange(bunker);
        _template = bunker[0];
        _printerPrefab ??= Device(_template.Printer, PrinterDevice, BoltFactories.Resin3dPrinterState);
        _laptopPrefab ??= Device(_template.Laptop, LaptopDevice, BoltFactories.Resin3dPrinterLaptopState);
        RegisterItem(PartsId, PartsPickup, "3D Printer Parts", "Parts of a broken 3D printer. Rebuild it from the blueprint book and power it with a solar panel.", _template.Printer.transform, 0.5f, Texture("PrinterIcon"));
        RegisterItem(LaptopId, LaptopPickup, "Printer Laptop", "The laptop that runs a 3D printer. Place it from the blueprint book and wire it to a powered printer.", _template.Laptop.transform, 1f, Texture("LaptopIcon"));
        Blueprint.Register(_printerPrefab.transform, _laptopPrefab.transform);
        foreach (var s in Stations) Setup(s);
        RLog.Msg("Bring the Printer blueprints registered");
    }

    public static void Init()
    {
        Activate();
        if (_template == null)
        {
            RLog.Error("No bunker 3D printers found");
            return;
        }

        AddInventoryModel(PartsId, _template.Printer.transform, 0.25f, PartsSlot);
        AddInventoryModel(LaptopId, _template.Laptop.transform, 0.5f, LaptopSlot);
        ItemTools.GetInventoryLayoutItemGroup(LaptopId).transform.localRotation = Quaternion.Euler(0, 180, 0);
        if (!BoltNetwork.isClient) SpawnAll();
    }

    static Texture2D Texture(string name)
    {
        var texture = AssetLoaders.LoadTextureFromAssembly(typeof(Printers).Assembly, $"Resources.{name}.png");
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }

    public static void Network(GameObject go, int id, string serializer = null)
    {
        var entity = go.GetComponent<BoltEntity>() ?? go.AddComponent<BoltEntity>();
        entity._prefabId = id;
        entity._sceneGuid = string.Empty;
        if (serializer != null)
        {
            entity._serializerGuid = serializer;
            entity._queryOptionIEntityBehaviour = QueryOptions.GetComponentsInChildren;
        }
        EntityManager.RegisterPrefab(entity);
    }

    static GameObject Device(Component source, int id, string serializer)
    {
        var t = Clone(source.transform);
        t.SetParent(Holder, false);
        t.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        t.gameObject.SetActive(true);
        Network(t.gameObject, id, serializer);
        return t.gameObject;
    }

    static void RegisterItem(int id, int entity, string name, string description, Transform source, float scale, Texture2D icon)
    {
        if (!ItemTools.IsItemRegistered(id))
        {
            var ui = ItemTools.CreateAndRegisterItem(id, name, 10, icon, description)._uiData;
            ui._outlineIcon = icon;
            ui._titlePlural = name;
            var data = ItemDatabaseManager.ItemById(id);
            data._type &= ~Sons.Items.Core.Types.CraftingMaterial;
        }
        LocalizationTools.ItemsTable.AddEntry($"I_{id}_PLURAL", name);
        var pickup = ItemTools.GetPickupPrefab(id);
        if (pickup && pickup.name == name + "Pickup") return;
        var layer = ItemTools.GetPickupPrefab(ItemTools.Identifiers.DeerHide).GetComponent<Sons.Gameplay.PickUp>()._itemRenderable.gameObject.layer;
        var model = Model(source, layer);
        model.transform.localScale = Vector3.one * scale;
        new ItemTools.ItemBuilder(model, ItemDatabaseManager.ItemById(id)).SetupPickup(model);
        Object.Destroy(model);
        pickup = ItemTools.GetPickupPrefab(id);
        pickup.name = name + "Pickup";
        pickup.SetParent(Holder, false);
        pickup.gameObject.SetActive(true);
        Network(pickup.gameObject, entity);
    }

    static void AddInventoryModel(int id, Transform source, float scale, Vector3 position)
    {
        if (ItemTools.GetInventoryLayoutItemGroup(id)) return;
        var model = Model(source, LayerMask.NameToLayer("Inventory"));
        foreach (var r in model.GetComponentsInChildren<MeshRenderer>()) r.gameObject.AddComponent<BoxCollider>();
        model.transform.localScale = Vector3.one * scale;
        new ItemTools.ItemBuilder(model, ItemDatabaseManager.ItemById(id)).AddInventoryItem(position);
        Object.Destroy(model);
        foreach (var r in ItemTools.GetInventoryLayoutItemGroup(id).GetComponentsInChildren<Endnight.Rendering.AssetReferenceRenderable>(true))
        {
            Pinned.Add(r.__onRenderableLoaded_k__BackingField = new UnityEngine.Events.UnityEvent<Transform>());
            Pinned.Add(r.__onRenderableUnloaded_k__BackingField = new UnityEngine.Events.UnityEvent());
        }
    }

    public static GameObject Model(Transform source, int layer)
    {
        var model = new GameObject(source.name + "Model");
        var meshes = new HashSet<string>();
        foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (!renderer || !filter.sharedMesh || filter.sharedMesh.name.Contains("Broken") || !meshes.Add(filter.sharedMesh.name)) continue;
            var part = new GameObject(filter.name) { layer = layer };
            part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            var t = part.transform;
            t.SetParent(model.transform, false);
            t.SetLocalPositionAndRotation(source.InverseTransformPoint(filter.transform.position), Quaternion.Inverse(source.rotation) * filter.transform.rotation);
            t.localScale = filter.transform.lossyScale;
        }
        return model;
    }

    public static Bounds LocalBounds(Transform root)
    {
        var bounds = new Bounds();
        var any = false;
        foreach (var f in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh && !f.sharedMesh.name.Contains("Broken") && f.GetComponent<MeshRenderer>()))
        {
            var m = f.sharedMesh.bounds;
            for (var i = 0; i < 8; i++)
            {
                var p = root.InverseTransformPoint(f.transform.TransformPoint(m.center + Vector3.Scale(m.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1))));
                if (any) bounds.Encapsulate(p);
                else bounds = new Bounds(p, Vector3.zero);
                any = true;
            }
        }
        return bounds;
    }

    public static Transform Clone(Transform source)
    {
        var go = source.gameObject;
        var active = go.activeSelf;
        go.SetActive(false);
        var clone = Object.Instantiate(go);
        go.SetActive(active);
        return clone.transform;
    }

    static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        var go = BoltNetwork.isRunning ? BoltNetwork.Instantiate(prefab, position, rotation).gameObject : Object.Instantiate(prefab, position, rotation);
        go.SetActive(true);
        return go;
    }

    static void Destroy(GameObject go)
    {
        var entity = go.GetComponent<BoltEntity>();
        if (entity && entity.isAttached) BoltNetwork.Destroy(go);
        else Object.Destroy(go);
    }

    static void Setup(Station s)
    {
        Breakable(s.Printer, BreakAmount, () => Broken(s, false));
        Breakable(s.Laptop, 1f, () => Broken(s, true));
    }

    static void Breakable(Component target, float amount, Action onBreak)
    {
        var collider = target.GetComponentsInChildren<Collider>(true).First(c => !c.isTrigger);
        var node = collider.GetComponent<DamageNode>() ?? collider.gameObject.AddComponent<DamageNode>();
        node._collider = collider;
        node._autoFindCollider = false;
        node.SetUseBreak(true);
        node.SetBreakAmount(amount);
        node.RepairBreak();
        node.RegisterImpactCallback((Action<IImpactSender, IImpactData>)((_, _) => Hit(target is Resin3dPrinter, collider)));
        node.RegisterBreakCallback((Action<IBreakSender, IImpactData>)((_, _) => onBreak()));
    }

    static Built Of(Component c) => Rebuilt.FirstOrDefault(b => b.Structure && b.Structure.gameObject == c.gameObject);

    public static DamageNode Node(Component target) => target.GetComponentInChildren<DamageNode>(true);

    static void Sound(string name, Vector3 position)
    {
        if (!FMOD_StudioSystem.TryGetInstance(out var fmod, false)) return;
        var sound = fmod.PlayOneShot(Sounds[name], position, null);
        if (name == "PrinterOn") Stop(sound).RunCoro();
    }

    static IEnumerator Stop(FMOD.Studio.EventInstance sound)
    {
        yield return new WaitForSeconds(PulseLength);
        sound.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
    }

    static void Sound(Component device, string action, Vector3 position) => Sound((device is Resin3dPrinter ? "Printer" : "Laptop") + action, position);

    static void Hit(bool printer, Collider collider)
    {
        var eye = LocalPlayer.MainCamTr.position;
        var point = collider.ClosestPoint(eye);
        Sound(printer ? "PrinterHit" : "LaptopHit", point);
        if (!printer) return;
        Sound("Sparks", point);
        ImpactEffectManager.SpawnSurfaceImpactObject(ImpactEffectManager.ParticleType.Sparks, point, Quaternion.LookRotation(eye - point), Vector3.zero, false);
    }

    static void SpawnAll()
    {
        foreach (var d in Drops.Where(d => d.Go)) Destroy(d.Go);
        Drops.Clear();
        foreach (var d in State.Drops) SpawnPickup(d.Id, new Vector3(d.X, d.Y, d.Z));
        Blueprint.Restore(State.Blueprints);
        RestoreBuilt().RunCoro();
    }

    static IEnumerator RestoreBuilt()
    {
        yield return null;
        var existing = Object.FindObjectsOfType<ElectricDeviceScrewStructure>().Where(s => Blueprint.IsOurs(s._recipe)).ToList();
        foreach (var p in State.Printers.ToList())
        {
            var position = new Vector3(p.X, p.Y, p.Z);
            if (existing.Any(s => s._recipe._id == p.Recipe && Vector3.Distance(s.transform.position, position) < 0.05f)) continue;
            Spawn(Blueprint.Recipe(p.Recipe)._builtPrefab, position, Quaternion.Euler(0, p.Yaw, 0));
        }
    }

    public static void Track(ElectricDeviceScrewStructure structure)
    {
        if (Rebuilt.Any(b => b.Structure == structure)) return;
        var t = structure.transform;
        var id = structure._recipe._id;
        var built = new Built { Structure = structure, Power = structure.GetComponent<PowerFlowIndicator>() };
        built.Placed = State.Printers.FirstOrDefault(p => p.Recipe == id && Vector3.Distance(new Vector3(p.X, p.Y, p.Z), t.position) < 0.05f && Rebuilt.All(b => b.Placed != p));
        if (built.Placed == null)
        {
            built.Placed = new Placed { Recipe = id, X = t.position.x, Y = t.position.y, Z = t.position.z, Yaw = t.eulerAngles.y };
            State.Printers.Add(built.Placed);
        }
        structure._device.ToggleState(true);
        Rebuilt.Add(built);
        if (!BoltNetwork.isClient) Attach(built, Spawn(built.IsPrinter ? _printerPrefab : _laptopPrefab, t.position, t.rotation));
    }

    static void Attach(Built b, GameObject device)
    {
        b.Printer = device.GetComponent<Resin3dPrinter>();
        b.Laptop = device.GetComponent<Resin3dPrinterLaptop>();
        if (b.Printer)
        {
            var p = b.Structure.transform.position;
            b.Printer._printerName = $"BringThePrinter_{p.x:F1}_{p.y:F1}_{p.z:F1}";
            b.Printer._connectedLaptop = null;
            if (!BoltNetwork.isClient) Restore(b);
            b.Printer.UpdateUI(true);
        }
        else b.Laptop._connectedPrinter = null;
        Power(b, false);
    }

    static void Adopt()
    {
        var free = Object.FindObjectsOfType<Resin3dPrinter>().Select(p => p.gameObject)
            .Concat(Object.FindObjectsOfType<Resin3dPrinterLaptop>().Select(l => l.gameObject)).ToList();
        foreach (var b in Rebuilt.Where(b => !b.Device))
        {
            var go = free.FirstOrDefault(g => (g.GetComponent<Resin3dPrinter>() != null) == b.IsPrinter && Vector3.Distance(g.transform.position, b.Structure.transform.position) < 0.1f);
            if (go) Attach(b, go);
        }
    }

    static void Prune() => Rebuilt.RemoveAll(b =>
    {
        if (b.Structure) return false;
        State.Printers.Remove(b.Placed);
        if (b.Device && !BoltNetwork.isClient) Destroy(b.Device.gameObject);
        return true;
    });

    static void SpawnPickup(int id, Vector3 position)
    {
        if (Physics.Raycast(position, Vector3.down, out var hit, 5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) position = hit.point;
        Drops.Add((id, Spawn(ItemTools.GetPickupPrefab(id).gameObject, position, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0))));
    }

    static void Broken(Station s, bool laptop)
    {
        if (BoltNetwork.isClient)
        {
            Node(laptop ? s.Laptop : s.Printer).RepairBreak();
            Net.Request(Stations.IndexOf(s), laptop);
            return;
        }
        if (laptop) DropLaptop(s);
        else Break(s);
        Broadcast();
    }

    static void Requested(int station, bool laptop)
    {
        if (station >= 0 && station < Stations.Count) Broken(Stations[station], laptop);
    }

    static void Break(Station s)
    {
        if (!Active(s.Printer)) return;
        Show(s.Printer, false);
        SpawnPickup(PartsId, s.Printer.transform.position + Vector3.up * 0.4f);
    }

    static void DropLaptop(Station s)
    {
        if (!Active(s.Laptop)) return;
        if (s.Printer.IsPrinting)
        {
            Node(s.Laptop).RepairBreak();
            return;
        }
        Show(s.Laptop, false);
        SpawnPickup(LaptopId, s.Laptop.transform.position + Vector3.up * 0.3f);
    }

    static void Show(Component device, bool on)
    {
        if (Active(device) == on) return;
        if (on) Node(device).RepairBreak();
        else
        {
            Exit(device);
            Sound(device, "Break", device.transform.position);
        }
        device.gameObject.SetActive(on);
    }

    static void Broadcast()
    {
        if (!BoltNetwork.isServer) return;
        var mask = 0;
        for (var i = 0; i < Stations.Count; i++)
            mask |= (Active(Stations[i].Printer) ? 1 : 0) << i * 2 | (Active(Stations[i].Laptop) ? 2 : 0) << i * 2;
        Net.Broadcast(mask);
    }

    static void Apply(int mask)
    {
        for (var i = 0; i < Stations.Count; i++)
        {
            Show(Stations[i].Printer, (mask >> i * 2 & 1) != 0);
            Show(Stations[i].Laptop, (mask >> i * 2 & 2) != 0);
        }
    }

    static bool Active(Component c) => c.gameObject.activeSelf;

    static float Distance(Component c) => Vector3.Distance(c.transform.position, LocalPlayer.Transform.position);

    public static void Tick()
    {
        if (_template == null || !LocalPlayer.IsInWorld || Time.time < _nextTick) return;
        _nextTick = Time.time + 0.25f;
        Prune();
        if (BoltNetwork.isClient && Rebuilt.Any(b => !b.Device)) Adopt();
        foreach (var b in Rebuilt) Power(b, b.Powered);
        foreach (var printer in Rebuilt.Where(b => b.Printer))
            Link(printer, printer.On == true ? Rebuilt.FirstOrDefault(l => l.Laptop && l.On == true && (!l.Laptop._connectedPrinter || l.Laptop._connectedPrinter == printer.Printer) && Wired(l.Wire, printer.Wire)) : null);
        foreach (var laptop in Rebuilt.Where(b => b.Laptop && !b.Laptop._connectedPrinter)) laptop.Laptop._connectedPrinter = null;

        if (Time.time < _nextScan || BoltNetwork.isClient) return;
        _nextScan = Time.time + 1f;
        foreach (var s in Stations.Where(s => (!Active(s.Printer) || !Active(s.Laptop)) && Distance(s.Printer) > RespawnDistance))
        {
            Show(s.Printer, true);
            Show(s.Laptop, true);
        }
        Broadcast();
    }

    static bool Wired(Wire from, Wire to)
    {
        var seen = new HashSet<IntPtr> { from.Pointer };
        var queue = new Queue<Wire>(new[] { from });
        while (queue.Count > 0)
            foreach (var w in queue.Dequeue()._connectedWires.ToArray().Select(c => c.TryCast<Wire>()).Where(c => c))
            {
                if (w.Pointer == to.Pointer) return true;
                if (seen.Add(w.Pointer) && !w.GetComponent<ElectricDevice>() && !w.GetComponentInChildren<PowerSource>(true)) queue.Enqueue(w);
            }
        return false;
    }

    static void Link(Built printer, Built laptop)
    {
        var current = printer.Printer._connectedLaptop;
        if (current == laptop?.Laptop || (!laptop?.Laptop && printer.Printer.IsPrinting)) return;
        if (current)
        {
            current._connectedPrinter = null;
            current._networkPrinterSet = false;
        }
        printer.Printer._connectedLaptop = laptop?.Laptop;
        printer.Printer._networkPrinterLaptopSet = false;
        if (laptop == null) return;
        laptop.Laptop._connectedPrinter = printer.Printer;
        laptop.Laptop._networkPrinterSet = false;
    }

    static void Power(Built b, bool on)
    {
        if (!b.Device) return;
        if (b.Printer && b.Printer.IsPrinting && on == (b.Held == true) && !BoltNetwork.isClient)
        {
            b.Held = !on && !b.Printer.IsPaused;
            if (on) b.Printer.ResumePrint();
            else if (b.Held == true) b.Printer.PausePrint();
        }
        if (b.On == on) return;
        if (!on) Exit(b.Device);
        if (b.On.HasValue) Sound(b.Device, on ? "On" : "Off", b.Structure.transform.position);
        b.On = on;
        foreach (var c in b.Device.GetComponentsInChildren<Canvas>(true)) c.enabled = on;
        foreach (var c in b.Device.GetComponents<Collider>().Where(c => c.isTrigger)) c.enabled = on;
        foreach (var l in b.Device.GetComponentsInChildren<Light>(true)) l.enabled = on;
    }

    static void Exit(Component device)
    {
        if (device is Resin3dPrinter printer) printer.GrabExit();
        else ((Resin3dPrinterLaptop)device).GrabExit();
    }

    public static Component Target()
    {
        var cam = LocalPlayer.MainCamTr;
        foreach (var hit in Physics.RaycastAll(cam.position, cam.forward, Reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide).OrderBy(h => h.distance))
        {
            var target = (Component)hit.collider.GetComponentInParent<Resin3dPrinterLaptop>() ?? hit.collider.GetComponentInParent<Resin3dPrinter>();
            if (target && Stations.Any(s => s.Printer == target || s.Laptop == target)) return target;
            if (!hit.collider.isTrigger) break;
        }
        return null;
    }

    public static void BreakDebug()
    {
        var target = Target();
        if (target) Node(target).ForceBreak();
    }

    public static void SpawnDebug()
    {
        if (_template == null || BoltNetwork.isClient) { RLog.Error("Load a game as host first"); return; }
        var pos = SonsTools.GetPositionInFrontOfPlayer(2f, 0f);
        var rotation = Quaternion.Euler(0, LocalPlayer.Transform.eulerAngles.y + 180f, 0);
        Spawn(Blueprint.Recipe(Blueprint.PrinterRecipeId)._builtPrefab, pos, rotation);
        Spawn(Blueprint.Recipe(Blueprint.LaptopRecipeId)._builtPrefab, pos + rotation * Vector3.right * 1.2f, rotation);
    }

    [HarmonyPatch(typeof(ScrewStructureManager), "Sons_Save_ISaveGameSerializer_Sons_Crafting_Structures_ScrewStructureManager_ScrewStructuresData__OnDeserialize")]
    static class LoadPatch
    {
        static void Prefix()
        {
            try { Activate(); }
            catch (Exception e) { RLog.Error(e); }
        }
    }

    [HarmonyPatch(typeof(PrefabDatabase), nameof(PrefabDatabase.Find))]
    static class FindPatch
    {
        static void Prefix(PrefabId id)
        {
            if (_template != null || id.Value < Blueprint.PrinterRecipeId - 1 || id.Value > LaptopPickup) return;
            try { Activate(); }
            catch (Exception e) { RLog.Error(e); }
        }
    }

    [HarmonyPatch(typeof(ScrewStructureDestruction), nameof(ScrewStructureDestruction.DestroyStructure))]
    static class DestroyPatch
    {
        static void Prefix(ScrewStructureDestruction __instance)
        {
            var built = Of(__instance);
            if (built == null || !built.Device) return;
            Exit(built.Device);
            if (BoltNetwork.isClient) return;
            Sound(built.Device, "Break", __instance.transform.position);
            SpawnPickup(built.IsPrinter ? PartsId : LaptopId, __instance.transform.position + Vector3.up * 0.4f);
            Destroy(built.Device.gameObject);
        }
    }

    [HarmonyPatch(typeof(ScrewStructureDestruction), nameof(ScrewStructureDestruction.IImpactReceiver_OnImpact))]
    static class HitPatch
    {
        static void Postfix(ScrewStructureDestruction __instance)
        {
            var built = Of(__instance);
            if (built != null) Hit(built.IsPrinter, built.Structure.GetComponent<BoxCollider>());
        }
    }

    [HarmonyPatch(typeof(PowerGrid), nameof(PowerGrid.StartPowerUpOrDownEvent))]
    static class GridSoundPatch
    {
        static bool Prefix(MonoBehaviour closestDevice) => !closestDevice || Of(closestDevice) == null;
    }

    [HarmonyPatch(typeof(ElectricDeviceScrewStructure), nameof(ElectricDeviceScrewStructure.Start))]
    static class StartPatch
    {
        static void Postfix(ElectricDeviceScrewStructure __instance)
        {
            if (Blueprint.IsOurs(__instance._recipe)) Track(__instance);
        }
    }
}
