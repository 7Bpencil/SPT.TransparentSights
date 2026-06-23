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
        private static TypedFieldInfo<ProceduralWeaponAnimation, GInterface200> __firearmAnimationData = new("_firearmAnimationData");
        private static TypedFieldInfo<ProceduralWeaponAnimation, bool> __isAiming = new("_isAiming");
        private static TypedFieldInfo<ProceduralWeaponAnimation, int> __pose = new("_pose");

        public FirearmController _firearmController { get { return __firearmController.Get(__instance); } set { __firearmController.Set(__instance, value); } }
        public GInterface200 _firearmAnimationData { get { return __firearmAnimationData.Get(__instance); } set { __firearmAnimationData.Set(__instance, value); } }
        public bool _isAiming { get { return __isAiming.Get(__instance); } set { __isAiming.Set(__instance, value); } }
        public int _pose { get { return __pose.Get(__instance); } set { __pose.Set(__instance, value); } }
    }

    public struct FirearmController_Proxy(FirearmController instance)
    {
        private readonly FirearmController __instance = instance;

        private static TypedFieldInfo<FirearmController, Player> __player = new("_player");

        public Player _player { get { return __player.Get(__instance); } set { __player.Set(__instance, value); } }
    }

	public struct WeaponPreview_Proxy(WeaponPreview instance)
	{
        private readonly WeaponPreview __instance = instance;

		private static TypedFieldInfo<WeaponPreview, GameObject> __gameObject_0 = new("gameObject_0");
		private static TypedFieldInfo<WeaponPreview, Item> __item_0 = new("item_0");

		public GameObject gameObject_0 { get { return __gameObject_0.Get(__instance); } set { __gameObject_0.Set(__instance, value); } }
		public Item item_0 { get { return __item_0.Get(__instance); } set { __item_0.Set(__instance, value); } }
	}

	public struct ItemSpecificationPanel_Proxy(ItemSpecificationPanel instance)
	{
        private readonly ItemSpecificationPanel __instance = instance;

		private static TypedFieldInfo<ItemSpecificationPanel, Item> __item_0 = new("item_0");

		public Item item_0 { get { return __item_0.Get(__instance); } set { __item_0.Set(__instance, value); } }
	}

	public struct InteractionButtonsContainer_Proxy(InteractionButtonsContainer instance)
	{
        private readonly InteractionButtonsContainer __instance = instance;

		private static TypedFieldInfo<InteractionButtonsContainer, SimpleContextMenuButton> __buttonTemplate = new("_buttonTemplate");
		private static TypedFieldInfo<InteractionButtonsContainer, RectTransform> __buttonsContainer = new("_buttonsContainer");

		public SimpleContextMenuButton _buttonTemplate { get { return __buttonTemplate.Get(__instance); } set { __buttonTemplate.Set(__instance, value); } }
		public RectTransform _buttonsContainer { get { return __buttonsContainer.Get(__instance); } set { __buttonsContainer.Set(__instance, value); } }
	}

    public struct ContextMenuButton_Proxy(ContextMenuButton instance)
	{
        private readonly ContextMenuButton __instance = instance;

		private static TypedFieldInfo<ContextMenuButton, TextMeshProUGUI> __text = new("_text");

		public TextMeshProUGUI _text { get { return __text.Get(__instance); } set { __text.Set(__instance, value); } }
	}

	public class Patch_PWA_method_23 : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.method_23));
        }

        [PatchPostfix]
        public static void Postfix(ProceduralWeaponAnimation __instance, bool forced = false)
		{
			try
			{
				if (!__instance)
				{
					Logger.LogWarning($"PWA null?");
					return;
				}
				var __instance__ = new ProceduralWeaponAnimation_Proxy(__instance);
				var firearmController = __instance__._firearmController;
				if (!firearmController)
				{
					Logger.LogWarning($"FirearmController null?");
					return;
				}
				var _firearmController = new FirearmController_Proxy(firearmController);
				var player = _firearmController._player;
				if (!player)
				{
					Logger.LogWarning($"Player null?");
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

				if (__instance.CurrentScope.IsOptic)
				{
					// when user switches between optic and collimator on top,
					// make sure that optic and collimator have correct transparency
					Plugin.Instance.OnAimingDisabled();
					return;
				}

				var scope = __instance.CurrentAimingMod;
				if (scope == null)
				{
					// happens with weapons that have scope "builtin" (PPSH for example)
					Plugin.Instance.OnAimingDisabled();
					return;
				}

				var scopeTemplateId = scope.Item.StringTemplateId;
				var scopeTransform = __instance.CurrentScope.Bone.transform.parent;
				Plugin.Instance.OnAimingEnabled(scopeTemplateId, scopeTransform);
			}
			catch (Exception e)
			{
				Logger.LogError(e);
			}
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
			var item = __instance__.item_0;
			if (item == null)
			{
				return;
			}
			if (item.Template is not SightsTemplateClass)
            {
                return;
            }

    		var templateId = item.StringTemplateId;

			void OnClick()
            {
				Plugin.Instance.SwitchScopeTransparencyMode(templateId);
				Plugin.Instance.UpdateAllPanels(templateId);
            }

			var buttonsContainer = new InteractionButtonsContainer_Proxy(____interactionButtonsContainer);

            var sprite = CacheResourcesPopAbstractClass.Pop<Sprite>("Characteristics/Icons/Modding");
			var startName = Plugin.Instance.GetScopeTransparencyModeName(templateId);
            var toggleButton = (ContextMenuButton)UnityEngine.Object.Instantiate(buttonsContainer._buttonTemplate, buttonsContainer._buttonsContainer, false);

            toggleButton.Show(startName, null, sprite, OnClick, null);
            ____interactionButtonsContainer.method_5(toggleButton);

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
			var item = __instance__.item_0;
			if (item == null)
			{
				return;
			}
			if (item.Template is not SightsTemplateClass)
            {
                return;
            }

    		var templateId = item.StringTemplateId;
			Plugin.Instance.RemovePanel(templateId, __instance);
        }
    }
}
