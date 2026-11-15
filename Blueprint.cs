using System.Collections;
using RedLoader;
using Sons.Crafting.Structures;
using Sons.Electricity;
using Sons.Weapon;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BringThePrinter;

public static class Blueprint
{
    public const int PrinterRecipeId = 7390739;
    public const int LaptopRecipeId = 7390740;
    const int LegLampRecipeId = 85;
    const string PageTitle = "BLUEPRINT_PAGE_3D_PRINTER";
    static readonly int[] PrinterIngredients = { Printers.PartsId, 416, 416, 418, 418, 418, 418, 419, 419 };
    static ScrewStructureRegistration _printer, _laptop;
    static Texture2D _page;

    public class Pending
    {
        public int Recipe = PrinterRecipeId;
        public float X, Y, Z, Yaw;
        public int Added;
    }

    public static bool IsOurs(StructureRecipe recipe) => recipe && (recipe._id == PrinterRecipeId || recipe._id == LaptopRecipeId);

    public static StructureRecipe Recipe(int id) => CustomBlueprintManager.GetProcessedRecipes().First(r => r._id == id);

    public static void Register(Transform printer, Transform laptop)
    {
        if (_printer == null)
        {
            _printer = Registration(printer, PrinterRecipeId, "3DPrinterRecipe", PrinterIngredients);
            _laptop = Registration(laptop, LaptopRecipeId, "3DPrinterLaptopRecipe", Printers.LaptopId);
            CustomBlueprintManager.OnCraftingNodeCreated.Subscribe(OnNodeCreated);
            _page = AssetLoaders.LoadTextureFromAssembly(typeof(Blueprint).Assembly, "Resources.BookPage.png");
            _page.hideFlags = HideFlags.HideAndDontSave;
        }

        CustomBlueprintManager.SetupObjectAsCraftingNode(_printer);
        CustomBlueprintManager.SetupObjectAsCraftingNode(_laptop);
        StructureCraftingSystem.TryGetInstance(out var system);
        system._recipeDatabase.TryGetRecipeById(LegLampRecipeId, out var lamp);
        var printerRecipe = Recipe(PrinterRecipeId);
        var laptopRecipe = Recipe(LaptopRecipeId);
        Built(printerRecipe, lamp, printer, 20f, new Vector3(1, -1, 1), 0.45f, Printers.PrinterEntity);
        Built(laptopRecipe, lamp, laptop, 5f, new Vector3(1, -1, 1), 0f, Printers.LaptopEntity);
        LocalizationTools.ItemsTable.AddEntry(PageTitle, "3D Printer");
        var pages = ItemTools.GetHeldPrefab(ItemTools.Identifiers.BlueprintBook).GetComponent<BlueprintBookController>()._pages._pages;
        for (var i = 0; i < pages.Count; i++)
            if (pages[i]._topRecipe == printerRecipe) return;
        CustomBlueprintManager.CreateBookPage(printerRecipe, laptopRecipe, _page);
        pages[pages.Count - 1]._pageTitleLocalizationId = PageTitle;
    }

    static ScrewStructureRegistration Registration(Transform source, int id, string name, params int[] ingredients)
    {
        var prefab = new GameObject("BringThePrinter" + id);
        var model = Printers.Model(source, 0).transform;
        model.SetParent(prefab.transform, false);
        var targets = ingredients.Length == 1
            ? new List<GameObject> { model.gameObject }
            : model.GetComponentsInChildren<MeshRenderer>(true).OrderBy(r => r.name != "Body").Select(r => r.gameObject).ToList();
        if (targets.Count != ingredients.Length) RLog.Warning($"{name} has {targets.Count} meshes, expected {ingredients.Length}");
        for (var i = 0; i < targets.Count && i < ingredients.Length; i++)
        {
            var ingredient = targets[i].AddComponent<StructureCraftingNodeIngredient>();
            ingredient.OnValidate();
            ingredient._itemId = ingredients[i];
            ingredient._requiresAllPreviousIngredients = true;
        }
        prefab.DontDestroyOnLoad().HideAndDontSave();
        return new ScrewStructureRegistration(prefab, id, name);
    }

    static void Built(StructureRecipe recipe, StructureRecipe lamp, Transform device, float drain, Vector3 corner, float hp, int entity)
    {
        recipe._alignToSurface = false;
        if (recipe._builtPrefab && recipe._builtPrefab.GetComponent<ElectricDeviceScrewStructure>()) return;
        if (recipe._builtPrefab) EntityManager.UnregisterPrefab(recipe._builtPrefab.GetComponent<BoltEntity>());
        var built = Object.Instantiate(lamp._builtPrefab, Printers.Holder);
        built.name = recipe.name.Replace("Recipe", "Structure");
        Object.DestroyImmediate(built.transform.Find("Renderables").gameObject);
        Object.DestroyImmediate(built.GetComponent<StructureDynamicParentColliderLodCallbacks>());
        foreach (var c in built.GetComponents<Collider>()) Object.DestroyImmediate(c);
        built.GetComponent<ElectricDeviceScrewStructure>()._recipe = recipe;
        var destruction = built.GetComponent<ScrewStructureDestruction>();
        destruction._renderablesRootOverride = null;
        destruction._impactMaterialType = AudioImpactMaterial.None;
        destruction._impactParticleType = Sons.VFX.ParticleTypes.HitStone;
        destruction._structureHp = destruction._currentHp = Mathf.Max(1, Mathf.CeilToInt(destruction._structureHp * hp));
        var light = built.GetComponent<ElectricLight>();
        light._lights.Clear();
        light._lightRenderers.Clear();
        light._powerDrain = drain;
        var flow = built.GetComponent<PowerFlowIndicator>();
        flow._hasPowerObjects.Clear();
        flow._noPowerObjects.Clear();
        flow._overloadedObjects.Clear();
        var off = built.GetComponent<ElectricDevicePoweredOff>();
        off._poweredOnObjects = new(0);
        off._poweredOnBehaviours = new(0);
        off._materialSwapData = new(0);

        Object.DestroyImmediate(built.transform.Find("Collision").gameObject);
        Object.DestroyImmediate(built.transform.Find("DeviceSwitch").gameObject);
        var bounds = Printers.LocalBounds(device);
        var box = built.AddComponent<BoxCollider>();
        box.center = bounds.center;
        box.size = bounds.size;
        var hook = built.transform.Find("WireHookPoint");
        hook.localPosition = bounds.center + Vector3.Scale(bounds.extents - Vector3.one * 0.03f, corner);
        var capsule = hook.GetComponent<CapsuleCollider>();
        capsule.center = Vector3.zero;
        capsule.height = 0.3f;
        Printers.Network(built, entity);
        recipe._builtPrefab = built;
    }

    static List<StructureCraftingNode> Nodes() => Resources.FindObjectsOfTypeAll<StructureCraftingNode>()
        .Where(n => IsOurs(n._recipe) && n.gameObject.scene.IsValid() && n.gameObject.scene.name != "DontDestroyOnLoad").ToList();

    public static List<Pending> Save() => Nodes().Select(n => new Pending
    {
        Recipe = n._recipe._id, X = n.transform.position.x, Y = n.transform.position.y, Z = n.transform.position.z, Yaw = n.transform.eulerAngles.y, Added = n.CountAddedIngredients()
    }).ToList();

    public static void Restore(List<Pending> pending) => RestoreRoutine(pending.ToList()).RunCoro();

    static IEnumerator RestoreRoutine(List<Pending> pending)
    {
        if (!StructureCraftingSystem.TryGetInstance(out var system)) yield break;
        var existing = Nodes();
        var placed = new List<(Pending Data, Vector3 Position)>();
        foreach (var p in pending)
        {
            var position = new Vector3(p.X, p.Y, p.Z);
            if (existing.Any(n => Vector3.Distance(n.transform.position, position) < 0.05f)) continue;
            system.PlaceStructureNode(Recipe(p.Recipe), position, Quaternion.Euler(0, p.Yaw, 0));
            placed.Add((p, position));
        }
        yield return null;
        var nodes = Nodes();
        foreach (var (data, position) in placed)
            nodes.FirstOrDefault(n => Vector3.Distance(n.transform.position, position) < 0.05f)?.SetAddedIngredients(data.Added, true);
    }

    static void OnNodeCreated(StructureCraftingNode node)
    {
        if (!IsOurs(node._recipe)) return;
        var root = node.transform;
        var bounds = new Bounds(root.position, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            bounds.Encapsulate(r.bounds);
            r.gameObject.AddComponent<StructureGhostSwapper>().Enable(false);
        }
        var box = node.GetComponent<BoxCollider>();
        box.center = bounds.center - root.position;
        box.size = bounds.size;
        var locator = root.Find("StructureInteractionObjects/UiLocator");
        if (locator) locator.localPosition = box.center;
    }
}
