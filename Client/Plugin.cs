//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using EFT.UI;
using EFT.UI.WeaponModding;
using Newtonsoft.Json;
using SevenBoldPencil.Common;
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityStandardAssets.ImageEffects;
using UnityEngine.Video;
using BlurSampleCount = UnityStandardAssets.ImageEffects.DepthOfField.BlurSampleCount;

namespace SevenBoldPencil.TransparentSights
{
    public readonly record struct CurrentAiming
    (
        Player Player,
        Firearms Firearms
    );

    public readonly record struct CurrentPatchedScope
    (
        Player Player,
        Firearms Firearms,
        Option<DOFData> DOFDataOption
    );

    public readonly record struct DOFData
    (
        DepthOfField DOF,
        SettingsDOF OriginalSettings
    );

    public readonly record struct SettingsDOF
    (
        bool enabled,
        BlurSampleCount blurSampleCount,
        float aperture,
        float focalLength,
        float focalSize,
        float foregroundOverlap,
        float maxBlurSize
    );

    public readonly record struct PatchedItem
    (
        List<PatchedRenderer> PatchedRenderers
    );

    public readonly record struct PatchedRenderer
    (
        Renderer Renderer,
        Material[] Original,
        Material[] Patched
    );

	public enum ScopeTransparencyMode
	{
		Disabled,
		Enabled,
		EnabledWithMount,
	}

    [BepInPlugin("7Bpencil.TransparentSights", "7Bpencil.TransparentSights", "0.2.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static readonly int _Cull = Shader.PropertyToID("_Cull");

        private const double SaveLagTime = 10;

        public static Plugin Instance;

        public static ConfigEntry<bool> MakeEntireWeaponTransparent;
        public static ConfigEntry<bool> DisableTransparencyInOptics;
        public static ConfigEntry<bool> DOF_enabled;
        public static ConfigEntry<BlurSampleCount> DOF_blurSampleCount;
        public static ConfigEntry<float> DOF_aperture;
        public static ConfigEntry<float> DOF_focalLength;
        public static ConfigEntry<float> DOF_focalSize;
        public static ConfigEntry<float> DOF_foregroundOverlap;
        public static ConfigEntry<float> DOF_maxBlurSize;
        public static ConfigEntry<float> DOF_maxBlurSize_optic;

		public ManualLogSource LoggerInstance;

        private string ConfigPath;
        private Shader SightShader;
        private Dictionary<string, ScopeTransparencyMode> TransparentScopes;
        private Dictionary<string, Dictionary<ItemSpecificationPanel, ContextMenuButton>> ScopesItemPanels;
        private Dictionary<int, PatchedItem> PatchedItems;
        private List<int> CurrentTransparentItems;
        private Option<CurrentPatchedScope> CurrentPatchedScope;
        private Option<CurrentAiming> CurrentAiming;
        private Option<double> LastSaveTime;

        private void Awake()
        {
            Instance = this;
			LoggerInstance = Logger;

            var generalGroup = "General";
            MakeEntireWeaponTransparent = Config.Bind<bool>(generalGroup, "Make entire weapon transparent", false);
            DisableTransparencyInOptics = Config.Bind<bool>(generalGroup, "Disable transparency in optics", false);
            DOF_enabled = Config.Bind<bool>(generalGroup, "Blur transparent sights", true);

            var dofGroup = "Depth of Field";
            DOF_maxBlurSize = Config.Bind<float>(dofGroup, "Blur Size", 0.94f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));
            DOF_maxBlurSize_optic = Config.Bind<float>(dofGroup, "Blur Size in Optic", 7.418873f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));

            var dofAdvancedGroup = "Depth of Field Advanced";
            DOF_blurSampleCount = Config.Bind<BlurSampleCount>(dofAdvancedGroup, "Blur Quality", BlurSampleCount.High);
            DOF_aperture = Config.Bind<float>(dofAdvancedGroup, "Aperture", 4, new ConfigDescription("", new AcceptableValueRange<float>(0, 50)));
            DOF_focalLength = Config.Bind<float>(dofAdvancedGroup, "Focal Length", 1.53f, new ConfigDescription("", new AcceptableValueRange<float>(0, 100)));
            DOF_focalSize = Config.Bind<float>(dofAdvancedGroup, "Focal Size", 0.61f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));
            DOF_foregroundOverlap = Config.Bind<float>(dofAdvancedGroup, "Foreground Overlap", 2.63f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));

            MakeEntireWeaponTransparent.SettingChanged += (_, _) => Change_TransparencySettings();
            DisableTransparencyInOptics.SettingChanged += (_, _) => Change_TransparencySettings();
            DOF_enabled.SettingChanged += (_, _) => Change_DOF_Enabled();
            DOF_blurSampleCount.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_aperture.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_focalLength.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_focalSize.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_foregroundOverlap.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_maxBlurSize.SettingChanged += (_, _) => Change_DOF_Settings();
            DOF_maxBlurSize_optic.SettingChanged += (_, _) => Change_DOF_Settings();

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SightShader = Shader.Find("Transparent/DepthZwriteDithered");
            ConfigPath = Path.Combine(assemblyDir, "config.json");
            TransparentScopes = LoadTransparentScopes(ConfigPath);
            ScopesItemPanels = new();
            PatchedItems = new();
            CurrentTransparentItems = new();

            new Patch_PWA_OnAimOrPoseChanged().Enable();
            new Patch_AssetPoolObject_OnDestroy().Enable();
            new Patch_LoddedSkin_Unskin().Enable();
            new Patch_ItemSpecificationPanel_Show().Enable();
            new Patch_ItemSpecificationPanel_Close().Enable();
            new Patch_Firearms_SetupMod().Enable();
            new Patch_Firearms_RemoveMod().Enable();
            new Patch_Firearms_SetRoundIntoWeapon().Enable();
#if DEBUG
            new Patch_FirearmController_Idling_DisableAimingOnReload().Enable();
#endif
        }

        public void Change_DOF_Settings()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope) && currentPatchedScope.DOFDataOption.Some(out var DOFData))
            {
                var isOptic = IsOptic(currentPatchedScope.Firearms);
                Set_DOF_Settings_Config(DOFData.DOF, Get_DOF_Config(isOptic));
            }
        }

        public void Change_DOF_Enabled()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope) && currentPatchedScope.DOFDataOption.Some(out var DOFData))
            {
                var isOptic = IsOptic(currentPatchedScope.Firearms);
                if (DOF_enabled.Value)
                {
                    Set_DOF_Settings_Config(DOFData.DOF, Get_DOF_Config(isOptic));
                }
                else
                {
                    Set_DOF_Settings_Config(DOFData.DOF, DOFData.OriginalSettings);
                }
            }
        }

        public SettingsDOF Get_DOF_Config(bool isOptic)
        {
            return new SettingsDOF
            (
                enabled: DOF_enabled.Value,
                blurSampleCount: DOF_blurSampleCount.Value,
                aperture: DOF_aperture.Value,
                focalLength: DOF_focalLength.Value,
                focalSize: DOF_focalSize.Value,
                foregroundOverlap: DOF_foregroundOverlap.Value,
                maxBlurSize: isOptic ? DOF_maxBlurSize_optic.Value : DOF_maxBlurSize.Value
            );
        }

        public void Set_DOF_Settings_Config(DepthOfField DOF, SettingsDOF settings)
        {
            DOF.enabled = settings.enabled;
            DOF.blurSampleCount = settings.blurSampleCount;
            DOF.aperture = settings.aperture;
            DOF.focalLength = settings.focalLength;
            DOF.focalSize = settings.focalSize;
            DOF.foregroundOverlap = settings.foregroundOverlap;
            DOF.maxBlurSize = settings.maxBlurSize;
        }

        public Dictionary<string, ScopeTransparencyMode> LoadTransparentScopes(string filePath)
        {
            if (SafeIO.ReadAllText(filePath).Ok(out var json, out var e))
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, ScopeTransparencyMode>>(json);
                return result;
            }
            else
            {
                Logger.LogError($"Failed to load transparent scopes, rolling back to default config: {e}");
            }

            return new()
            {
                { "616442e4faa1272e43152193", ScopeTransparencyMode.EnabledWithMount },
                { "5b3116595acfc40019476364", ScopeTransparencyMode.EnabledWithMount },
                { "58d399e486f77442e0016fe7", ScopeTransparencyMode.EnabledWithMount },
                { "61657230d92c473c770213d7", ScopeTransparencyMode.EnabledWithMount },
                { "609bab8b455afd752b2e6138", ScopeTransparencyMode.Disabled },
            };
        }

        public void SaveTransparentScopesToFile(string filePath, Dictionary<string, ScopeTransparencyMode> transparentScopes)
        {
            var json = JsonConvert.SerializeObject(transparentScopes, Formatting.Indented);
            SafeIO.WriteAllTextAsync(filePath, json);
        }

#if DEBUG
        public bool IsFrozen;
#endif
        public void Update()
        {
            // SaveLagTime and LastSaveTime are needed to not write to file
            // every time user changes scope transparency mode

            if (LastSaveTime.Some(out var lastSaveTime))
            {
                if (Time.realtimeSinceStartupAsDouble - lastSaveTime >= SaveLagTime)
                {
                    SaveTransparentScopesToFile(ConfigPath, TransparentScopes);
                    LastSaveTime = default;
                }
            }
#if DEBUG
            if (Input.GetKeyDown(KeyCode.F13))
            {
                IsFrozen = !IsFrozen;
                Time.timeScale = IsFrozen ? 0.01f : 1f;
            }
#endif
        }

        public static ScopeTransparencyMode GetNextScopeTransparencyMode(ScopeTransparencyMode value)
        {
            return value switch
            {
        		ScopeTransparencyMode.Disabled => ScopeTransparencyMode.Enabled,
        		ScopeTransparencyMode.Enabled => ScopeTransparencyMode.EnabledWithMount,
        		ScopeTransparencyMode.EnabledWithMount => ScopeTransparencyMode.Disabled,
                _ => throw new ArgumentException($"Unknown ScopeTransparencyMode: {value}"),
            };
        }

		private string GetScopeTransparencyModeName(ScopeTransparencyMode value)
		{
            return value switch
            {
        		ScopeTransparencyMode.Disabled => "TRANSP. OFF",
        		ScopeTransparencyMode.Enabled => "TRANSP. ON",
        		ScopeTransparencyMode.EnabledWithMount => "TRANSP. ON + MOUNT",
                _ => throw new ArgumentException($"Unknown ScopeTransparencyMode: {value}"),
            };
		}

        public ScopeTransparencyMode GetScopeTransparencyMode(string scopeTemplateId)
        {
            if (TransparentScopes.TryGetValue(scopeTemplateId, out var transparencyMode))
            {
                return transparencyMode;
            }

            // by default all sights are transparent
            return ScopeTransparencyMode.Enabled;
        }

        public string GetScopeTransparencyModeName(string scopeTemplateId)
        {
            var mode = GetScopeTransparencyMode(scopeTemplateId);
            var modeName = GetScopeTransparencyModeName(mode);
            return modeName;
        }

        public void SwitchScopeTransparencyMode(string scopeTemplateId)
        {
            // notice that we dont immediately reflect this change in CurrentPatchedScope,
            // because its impossible to change scope setting while ADS (player needs to open inventory first),
            // so OnAimingEnabled can take care of that

            var currentMode = GetScopeTransparencyMode(scopeTemplateId);
            var nextMode = GetNextScopeTransparencyMode(currentMode);
            TransparentScopes[scopeTemplateId] = nextMode;

            LastSaveTime = new(Time.realtimeSinceStartupAsDouble);
        }

        public void AddPanel(string scopeTemplateId, ItemSpecificationPanel panel, ContextMenuButton toggleButton)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Add(panel, toggleButton);
            }
            else
            {
                ScopesItemPanels.Add(scopeTemplateId, new(){{ panel, toggleButton }});
            }
        }

        public void RemovePanel(string scopeTemplateId, ItemSpecificationPanel panel)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Remove(panel);
                if (panels.Count == 0)
                {
                    ScopesItemPanels.Remove(scopeTemplateId);
                }
            }
        }

        public void UpdateAllPanels(string scopeTemplateId)
        {
            var modeName = GetScopeTransparencyModeName(scopeTemplateId);
            var panels = ScopesItemPanels[scopeTemplateId];
            foreach (var (panel, toggleButton) in panels)
            {
                toggleButton._text.text = modeName;
                panel.RecreateAttributeBars();
            }
        }

        public void OnAimingEnabled(Player player, Firearms firearms)
        {
#if DEBUG
            // infinite stamina for testing
            player.Physical.Stamina.Multiplier = 0;
            player.Physical.HandsStamina.Multiplier = 0;
#endif
            LogInfo("OnAimingEnabled");

            if (CurrentPatchedScope.HasValue)
            {
                OnAimingDisabled();
            }

            var isOptic = IsOptic(firearms);
            RebuildCurrentTransparentItems(player, firearms, isOptic);

            if (CurrentTransparentItems.Count != 0)
            {
                var DOFDataOption = TryGetDOFData();
                CurrentPatchedScope = new(new CurrentPatchedScope
                (
                    Player: player,
                    Firearms: firearms,
                    DOFDataOption: DOFDataOption
                ));
                if (DOF_enabled.Value && DOFDataOption.Some(out var DOFData))
                {
                    Set_DOF_Settings_Config(DOFData.DOF, Get_DOF_Config(isOptic));
                }
            }

            CurrentAiming = new(new CurrentAiming
            (
                Player: player,
                Firearms: firearms
            ));
        }

        public Option<DOFData> TryGetDOFData()
        {
            var DOF = CameraManager.Instance._depthOfField;

            // for some reason DOF is sometimes null...
            if (DOF)
            {
                var originalSettings = new SettingsDOF
                (
                    enabled: DOF.enabled,
                    blurSampleCount: DOF.blurSampleCount,
                    aperture: DOF.aperture,
                    focalLength: DOF.focalLength,
                    focalSize: DOF.focalSize,
                    foregroundOverlap: DOF.foregroundOverlap,
                    maxBlurSize: DOF.maxBlurSize
                );
                return new(new(DOF, originalSettings));
            }

            return default;
        }

        public bool IsOptic(Firearms firearms)
        {
            return firearms.ProceduralWeaponAnimation.CurrentScope.IsOptic;
        }

        // weapon can change between OnAimingDisabled and OnAimingEnabled,
        // so we have to update a list of items that get transparent,
        // hopefully its not that expensive
        public void RebuildCurrentTransparentItems(Player player, Firearms firearms, bool isOptic)
        {
			if (isOptic && DisableTransparencyInOptics.Value)
			{
				return;
			}

            var weaponPrefab = firearms.WeaponPrefab;
            if (MakeEntireWeaponTransparent.Value)
            {
                var hands = player.PlayerBody.BodySkins[EBodyModelPart.Hands];
                TryPatchItem(hands, PatchRenderers);
                TryPatchItem(weaponPrefab, PatchRenderers);
                if (weaponPrefab.ContainerCollectionView != null)
                {
                    foreach (var (container, containerData) in weaponPrefab.ContainerCollectionView.ContainerBones)
                    {
                        // empty slots or slots with invisible items have nulls (soft armor, helmet plates, etc)
                        if (containerData.Item == null)
                        {
                            continue;
                        }
                        if (!containerData.ItemView)
                        {
                            continue;
                        }
                        if (containerData.ItemView.TryGetComponent<AssetPoolObject>(out var subItemAssetPoolObject))
                        {
                            TryPatchMod(subItemAssetPoolObject);
                        }
                    }
                }
                foreach (var bullet in firearms._ammoObjectInWeapon)
                {
                    TryPatchItem(bullet, PatchRenderers);
                }
                // this one is important for revolvers, test:
                // ADS, shoot 2 times, un ADS, then ADS again, you will see
                foreach (var bullet in firearms._shellsInShellPort)
                {
                    TryPatchItem(bullet, PatchRenderers);
                }
            }
            else
            {
                var pwa = firearms.ProceduralWeaponAnimation;
    			var currentAimingMod = pwa.CurrentAimingMod;
    			if (currentAimingMod == null)
    			{
    				// happens with weapons that have scope "builtin" (PPSH and UZI for example)
    				return;
    			}

                var scopeItem = currentAimingMod.Item;
    			var scopeTemplateId = scopeItem.StringTemplateId;
    			var scopeTransform = pwa.CurrentScope.Bone.transform.parent;
                var scopeTransparencyMode = GetScopeTransparencyMode(scopeTemplateId);

                LogInfo("Sight:", scopeTemplateId, scopeTransparencyMode);

                if (scopeTransparencyMode == ScopeTransparencyMode.Enabled)
                {
                    if (scopeTransform.TryGetComponent<AssetPoolObject>(out var scopeVisual))
                    {
                        TryPatchCompoundItem(scopeItem, scopeVisual, weaponPrefab);
                    }
                }
                if (scopeTransparencyMode == ScopeTransparencyMode.EnabledWithMount)
                {
                    if (TryGetScopeMount(scopeItem, weaponPrefab).Some(out var mountData))
                    {
                        var (mountItem, mountVisual) = mountData;
                        TryPatchCompoundItem(mountItem, mountVisual, weaponPrefab);
                    }
                    else if (scopeTransform.TryGetComponent<AssetPoolObject>(out var scopeVisual))
                    {
                        TryPatchCompoundItem(scopeItem, scopeVisual, weaponPrefab);
                    }
                }
            }
        }

        // we get scope mount Item from scope parent slot,
        // then we get scope mount AssetPoolObject from WeaponPrefab
        // by keying list of all slots by slot that contains mount itself,
        // looks complicated, but most of the code is null checks (thanks BSG)
        public Option<(Item, AssetPoolObject)> TryGetScopeMount(Item scope, WeaponPrefab weaponPrefab)
        {
            if (!GetParentSlot(scope).Some(out var mountScopeSlot))
            {
                return default;
            }

            var mountItem = mountScopeSlot.ParentItem;
            if (mountItem == null)
            {
                return default;
            }
            if (!GetParentSlot(mountItem).Some(out var mountParentSlot))
            {
                return default;
            }

            var allWeaponContainers = weaponPrefab.ContainerCollectionView.ContainerBones;
            if (!allWeaponContainers.TryGetValue(mountParentSlot, out var containerData))
            {
                return default;
            }
            if (containerData.Item == null)
            {
                return default;
            }
            if (!containerData.ItemView)
            {
                return default;
            }
            if (!containerData.ItemView.TryGetComponent<AssetPoolObject>(out var mountAssetPoolObject))
            {
                return default;
            }

            return new((mountItem, mountAssetPoolObject));
        }

        public Option<Slot> GetParentSlot(Item item)
        {
            var currentAddress = item.CurrentAddress;
            if (currentAddress == null)
            {
                return default;
            }
            if (currentAddress is not SlotItemAddress slotAddress)
            {
                return default;
            }

            var parentSlot = slotAddress.Slot;
            if (parentSlot == null)
            {
                return default;
            }

            return new(parentSlot);
        }

        // some scopes can have subitems, examples:
        // - rubber eyecups on some optic scopes
        // - iron sight attachment on acog
        public void TryPatchCompoundItem(Item item, AssetPoolObject assetPoolObject, WeaponPrefab weaponPrefab)
        {
            TryPatchItem(assetPoolObject, PatchRenderers);

            if (item is not CompoundItem compoundItem)
            {
                return;
            }

            var allWeaponContainers = weaponPrefab.ContainerCollectionView.ContainerBones;
            foreach (var slot in compoundItem.Slots)
            {
                var containedItem = slot.ContainedItem;
                if (containedItem == null)
                {
                    continue;
                }
                if (!allWeaponContainers.TryGetValue(slot, out var containerData))
                {
                    continue;
                }
                if (containerData.Item == null)
                {
                    continue;
                }
                if (!containerData.ItemView)
                {
                    continue;
                }
                if (containerData.ItemView.TryGetComponent<AssetPoolObject>(out var subItemAssetPoolObject))
                {
                    TryPatchCompoundItem(containedItem, subItemAssetPoolObject, weaponPrefab);
                }
            }
        }

        public void TryPatchMod(AssetPoolObject assetPoolObject)
        {
            TryPatchItem(assetPoolObject, PatchRenderers);

            if (assetPoolObject is MagazineInHandsVisualController mag)
            {
                var magazineInHandsVisual = new MagazineInHandsVisualController_Proxy(mag)._magazineInHandsVisual;
                if (magazineInHandsVisual is SpringMagazineVisual boxMagazine)
                {
                    foreach (var bullet in boxMagazine._ammoPoolObjects)
                    {
                        TryPatchItem(bullet, PatchRenderers);
                    }
                }
                if (magazineInHandsVisual is BeltMagazineInHandsVisual beltBoxMagazine)
                {
                    foreach (var bullet in beltBoxMagazine._ammoPoolObjects)
                    {
                        TryPatchItem(bullet, PatchRenderers);
                    }
                }
            }
        }

        public void Change_TransparencySettings()
        {
            if (CurrentAiming.Some(out var currentAiming))
            {
                OnAimingEnabled(currentAiming.Player, currentAiming.Firearms);
            }
        }

        public void OnAimingDisabled()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                LogInfo("OnAimingDisabled");

                foreach (var tranparentItem in CurrentTransparentItems)
                {
                    ForPatchedItem(tranparentItem, SetOriginalMaterials);
                }
                CurrentTransparentItems.Clear();

                if (currentPatchedScope.DOFDataOption.Some(out var DOFData))
                {
                    Set_DOF_Settings_Config(DOFData.DOF, DOFData.OriginalSettings);
                }
                CurrentPatchedScope = default;
            }

            CurrentAiming = default;
        }

        public void TryPatchItem<T>(T item, Func<T, List<PatchedRenderer>> patcher) where T : MonoBehaviour
        {
            if (!item)
            {
                return;
            }
            var instanceID = item.gameObject.GetInstanceID();
            if (!PatchedItems.ContainsKey(instanceID))
            {
                var patchedRenderers = patcher(item);
                var patchedItem = new PatchedItem(patchedRenderers);
                PatchedItems.Add(instanceID, patchedItem);
            }
            CurrentTransparentItems.Add(instanceID);
            ForPatchedItem(instanceID, SetPatchedMaterials);
        }

        public List<PatchedRenderer> PatchRenderers(AssetPoolObject assetPoolObject)
        {
            var renderers = assetPoolObject.Renderers;
            var result = new List<PatchedRenderer>(renderers.Count);
            foreach (var renderer in renderers)
            {
                if (PatchRenderer(renderer).Some(out var patchedRenderer))
                {
    				result.Add(patchedRenderer);
                }
            }

            return result;
        }

        public List<PatchedRenderer> PatchRenderers(LoddedSkin skin)
        {
            var lods = skin._lods;
            var result = new List<PatchedRenderer>();
            foreach (var lod in lods)
            {
                if (PatchRenderer(lod.SkinnedMeshRenderer).Some(out var patchedRenderer))
                {
    				result.Add(patchedRenderer);
                }
            }

            return result;
        }

		public Option<PatchedRenderer> PatchRenderer(Renderer renderer)
		{
            if (!renderer)
            {
                return default;
            }

            var oldMaterials = renderer.materials;
            if (oldMaterials == null)
            {
                return default;
            }

            LogInfo("patch renderer:", renderer.name);

            var newMaterials = new Material[oldMaterials.Length];
            for (var i = 0; i < oldMaterials.Length; i++)
            {
                var oldMaterial = oldMaterials[i];
                if (oldMaterial && IsOpaqueMaterial(oldMaterial))
                {
                    var newMaterial = new Material(SightShader);
                    newMaterial.CopyPropertiesFromMaterial(oldMaterial);
                    newMaterial.SetFloat(_Cull, 2); // set backface culling, because some scopes have front face culling for some reasons
                    newMaterials[i] = newMaterial;
                }
                else
                {
                    newMaterials[i] = oldMaterial;
                }
            }

            return new(new PatchedRenderer
            (
                Renderer: renderer,
                Original: oldMaterials,
                Patched: newMaterials
            ));
		}

        public bool IsOpaqueMaterial(Material material)
        {
            var shaderName = material.shader.name;
            return
                shaderName == "p0/Reflective/Bumped Specular SMap" ||
                shaderName == "p0/Reflective/Specular" ||
                shaderName == "CW FX/BackLens" ||
                shaderName == "Unlit/Color2";
        }

        public void ForPatchedItem(int instanceID, Action<PatchedItem> doAction)
        {
            if (PatchedItems.TryGetValue(instanceID, out var patchedItem))
            {
                doAction(patchedItem);
            }
        }

        public void SetPatchedMaterials(PatchedItem patchedItem)
        {
            foreach (var patchedRenderer in patchedItem.PatchedRenderers)
            {
                patchedRenderer.Renderer.materials = patchedRenderer.Patched;
            }
        }

        public void SetOriginalMaterials(PatchedItem patchedItem)
        {
            foreach (var patchedRenderer in patchedItem.PatchedRenderers)
            {
                patchedRenderer.Renderer.materials = patchedRenderer.Original;
            }
        }

        public void OnSetupMod(Firearms firearms, GameObject modObject)
        {
            if (!MakeEntireWeaponTransparent.Value)
            {
                return;
            }
            if (!CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                return;
            }
            if (firearms != currentPatchedScope.Firearms)
            {
                return;
            }

			if (!modObject.TryGetComponent<AssetPoolObject>(out var assetPoolObject))
            {
                return;
            }

            TryPatchMod(assetPoolObject);

			LogInfo("OnSetupMod:", assetPoolObject.name);
        }

        public void OnRemoveMod(Firearms firearms, Slot slot)
        {
            if (!MakeEntireWeaponTransparent.Value)
            {
                return;
            }
            if (!CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                return;
            }
            if (firearms != currentPatchedScope.Firearms)
            {
                return;
            }

			var viewForSlot = firearms.ContainerCollectionView.GetViewForSlot(slot);
			var index = viewForSlot.Bone.childCount - 1;
			var child = viewForSlot.Bone.GetChild(index);

			if (!child.TryGetComponent<AssetPoolObject>(out var assetPoolObject))
			{
                return;
			}

            var instanceID = assetPoolObject.gameObject.GetInstanceID();
            if (CurrentTransparentItems.Remove(instanceID))
            {
                // TODO not sure about bullets in magazines
                ForPatchedItem(instanceID, SetOriginalMaterials);
            }

			LogInfo("OnRemoveMod:", assetPoolObject.name);
        }

        public void SetRoundIntoWeapon(Firearms firearms, int chamberNumber)
        {
            if (!MakeEntireWeaponTransparent.Value)
            {
                return;
            }
            if (!CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                return;
            }
            if (currentPatchedScope.Firearms != firearms)
            {
                return;
            }

            var bullet = firearms._ammoObjectInWeapon[chamberNumber];
            TryPatchItem(bullet, PatchRenderers);

			LogInfo("SetRoundIntoWeapon");
        }

        public void OnAssetPoolObjectDestroyed(AssetPoolObject assetPoolObject)
        {
            var instanceID = assetPoolObject.gameObject.GetInstanceID();
            OnPatchedItemDestroyed(instanceID);
        }

        public void OnSkinDestroyed(LoddedSkin skin)
        {
            var instanceID = skin.gameObject.GetInstanceID();
            OnPatchedItemDestroyed(instanceID);
        }

        public void OnPatchedItemDestroyed(int instanceID)
        {
            if (PatchedItems.Remove(instanceID, out var patchedItem))
            {
                CleanPatchedRenderers(patchedItem.PatchedRenderers);
    			LogInfo("OnPatchedItemDestroyed:", instanceID);
            }
        }

        public void CleanPatchedRenderers(List<PatchedRenderer> patchedRenderers)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                foreach (var patched in patchedRenderer.Patched)
                {
                    Destroy(patched);
                }
            }

            patchedRenderers.Clear();
        }

        public void LogInfo<A>(A a)
        {
#if DEBUG
			Logger.LogInfo(a);
#endif
        }

        public void LogInfo<A, B>(A a, B b)
        {
#if DEBUG
			Logger.LogInfo($"{a} {b}");
#endif
        }

        public void LogInfo<A, B, C>(A a, B b, C c)
        {
#if DEBUG
			Logger.LogInfo($"{a} {b} {c}");
#endif
        }
    }
}
