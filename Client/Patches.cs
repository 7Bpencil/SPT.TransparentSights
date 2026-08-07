//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using Diz.Skinning;
using EFT;
using EFT.Animations;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Visual;
using EFT.UI;
using EFT.UI.WeaponModding;
using EFT.Utilities;
using SevenBoldPencil.Common;
using System;
using System.Reflection;
using System.Threading;
using System.Collections.Generic;
using SPT.Reflection.Patching;
using TMPro;
using HarmonyLib;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace SevenBoldPencil.TransparentSights
{
    public struct ProceduralWeaponAnimation_Proxy(ProceduralWeaponAnimation instance)
    {
        private readonly ProceduralWeaponAnimation __instance = instance;

        private static TypedFieldInfo<ProceduralWeaponAnimation, FirearmController> __firearmController = new("_firearmController");
        private static TypedFieldInfo<ProceduralWeaponAnimation, bool> __isAiming = new("_isAiming");

        public FirearmController _firearmController { get { return __firearmController.Get(__instance); } set { __firearmController.Set(__instance, value); } }
        public bool _isAiming { get { return __isAiming.Get(__instance); } set { __isAiming.Set(__instance, value); } }
    }

	public struct MagazineInHandsVisualController_Proxy(MagazineInHandsVisualController instance)
	{
        private readonly MagazineInHandsVisualController __instance = instance;

		private static TypedFieldInfo<MagazineInHandsVisualController, MagazineInHandsVisual> __magazineInHandsVisual = new("_magazineInHandsVisual");

		public MagazineInHandsVisual _magazineInHandsVisual { get { return __magazineInHandsVisual.Get(__instance); } set { __magazineInHandsVisual.Set(__instance, value); } }
	}

	public struct ItemSpecificationPanel_Proxy(ItemSpecificationPanel instance)
	{
        private readonly ItemSpecificationPanel __instance = instance;

		private static TypedFieldInfo<ItemSpecificationPanel, Item> __item = new("_item");

		public Item _item { get { return __item.Get(__instance); } set { __item.Set(__instance, value); } }
	}

	public class Patch_PWA_OnAimOrPoseChanged : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.OnAimOrPoseChanged));
        }

        [PatchPostfix]
        public static void Postfix(ProceduralWeaponAnimation __instance, bool forced = false)
		{
			if (!__instance)
			{
				return;
			}

			var __instance__ = new ProceduralWeaponAnimation_Proxy(__instance);
			var firearmController = __instance__._firearmController;
			if (!firearmController)
			{
				return;
			}

			var player = firearmController._player;
			if (!player)
			{
				return;
			}

			if (!player.IsYourPlayer)
			{
				return;
			}

			if (!__instance__._isAiming)
			{
				Plugin.Instance.OnAimingDisabled();
				return;
			}

			var firearms = firearmController.Firearms;
			Plugin.Instance.OnAimingEnabled(player, firearms);
		}
	}

	// this one is usually called when player starts or finishes raid
	public class Patch_AssetPoolObject_OnDestroy : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AssetPoolObject), nameof(AssetPoolObject.OnDestroy));
        }

        [PatchPrefix]
        public static void Prefix(AssetPoolObject __instance)
		{
			Plugin.Instance.OnAssetPoolObjectDestroyed(__instance);
		}
	}

	// this is used right before lodded skin is destroyed
	public class Patch_LoddedSkin_Unskin : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LoddedSkin), nameof(LoddedSkin.Unskin));
        }

        [PatchPrefix]
        public static void Prefix(LoddedSkin __instance)
		{
			Plugin.Instance.OnSkinDestroyed(__instance);
		}
	}

	public class Patch_ItemSpecificationPanel_Show : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Show));
        }

        [PatchPostfix]
        private static void Postfix(ItemSpecificationPanel __instance, InteractionButtonsContainer ____interactionButtonsContainer)
        {
			var __instance__ = new ItemSpecificationPanel_Proxy(__instance);
			var item = __instance__._item;
			if (item == null)
			{
				return;
			}
			if (item.Template is not SightModTemplate)
            {
                return;
            }

    		var templateId = item.StringTemplateId;

			void OnClick()
            {
				Plugin.Instance.SwitchScopeTransparencyMode(templateId);
				Plugin.Instance.UpdateAllPanels(templateId);
            }

            var sprite = ResourcesCache.Pop<Sprite>("Characteristics/Icons/Modding");
			var startName = Plugin.Instance.GetScopeTransparencyModeName(templateId);
            var toggleButton = (ContextMenuButton)UnityEngine.Object.Instantiate(____interactionButtonsContainer._buttonTemplate, ____interactionButtonsContainer._buttonsContainer, false);

            toggleButton.Show(startName, null, sprite, OnClick, null);
            ____interactionButtonsContainer.BindButton(toggleButton);

			Plugin.Instance.AddPanel(templateId, __instance, toggleButton);
        }
    }

	public class Patch_ItemSpecificationPanel_Close : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Close));
        }

        [PatchPrefix]
        private static void Prefix(ItemSpecificationPanel __instance)
        {
			var __instance__ = new ItemSpecificationPanel_Proxy(__instance);
			var item = __instance__._item;
			if (item == null)
			{
				return;
			}
			if (item.Template is not SightModTemplate)
            {
                return;
            }

    		var templateId = item.StringTemplateId;
			Plugin.Instance.RemovePanel(templateId, __instance);
        }
    }

	public class Patch_Firearms_SetupMod : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Firearms), nameof(Firearms.SetupMod));
        }

        [PatchPostfix]
        private static void Postfix(Firearms __instance, Slot slot, GameObject modObject)
		{
			if (modObject.TryGetComponent<AssetPoolObject>(out var assetPoolObject))
			{
				Plugin.Instance.OnSetupMod(__instance.WeaponPrefab, assetPoolObject);
			}
		}
	}

	public class Patch_Firearms_RemoveMod : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Firearms), nameof(Firearms.RemoveMod));
        }

        [PatchPrefix]
        private static void Prefix(Firearms __instance, Slot slot)
		{
			var viewForSlot = __instance.ContainerCollectionView.GetViewForSlot(slot);
			var index = viewForSlot.Bone.childCount - 1;
			var child = viewForSlot.Bone.GetChild(index);
			if (child.TryGetComponent<AssetPoolObject>(out var assetPoolObject))
			{
				Plugin.Instance.OnRemoveMod(__instance.WeaponPrefab, assetPoolObject);
			}
		}
	}

	public class Patch_Firearms_SetRoundIntoWeapon : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Firearms), nameof(Firearms.SetRoundIntoWeapon));
        }

        [PatchPostfix]
		private static void Postfix(Firearms __instance, Ammo ammo, int chamberNumber = 0)
		{
			Plugin.Instance.SetRoundIntoWeapon(__instance, chamberNumber);
		}
	}

#if DEBUG
	public class Patch_FirearmController_Idling_DisableAimingOnReload : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirearmController.Idling), nameof(FirearmController.Idling.DisableAimingOnReload));
        }

        [PatchPrefix]
        private static bool Prefix()
		{
			// keep ADS on reload no matter weapon mastery for testing
			return false;
		}
	}
#endif
}
