using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Events;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using static Rocket.Unturned.Events.UnturnedPlayerEvents;
using System;
using System.Linq;

namespace KashiLootBox
{
    public class KashiLootBoxConfiguration : IRocketPluginConfiguration
    {
        public string LootBoxDroppedMessage;
        public string PlayerKilledMessage;
        public string PlayerDiedMessage;
        public string OpenAttemptMessage;
        public string PointGestureMessage;
        public string InventoryFullMessage;
        public float EffectRadius;

        public void LoadDefaults()
        {
            LootBoxDroppedMessage = "Bir oyuncu öldürdünüz, loot kutusu oluşturuldu!";
            PlayerKilledMessage = "Bir oyuncu öldürdünüz, loot kutusu oluşturuldu!";
            PlayerDiedMessage = "Öldünüz, loot kutusu oluşturuldu!";
            OpenAttemptMessage = "Kutuya bakarak point emotesi yapın.";
            PointGestureMessage = "Kutuyu kırmak için point emotesi yapın.";
            InventoryFullMessage = "Envanteriniz dolu, bazı eşyalar yere düştü!";
            EffectRadius = 2f;
        }
    }

    public class KashiLootBox : RocketPlugin<KashiLootBoxConfiguration>
    {
        public static KashiLootBox Instance;
        private const ushort LootBoxID = 10329;
        private const ushort EffectID = 19692;
        private const float DespawnTime = 90f;

        private Dictionary<CSteamID, bool> activeEffects = new Dictionary<CSteamID, bool>();

        protected override void Load()
        {
            Instance = this;
            UnturnedPlayerEvents.OnPlayerDeath += OnPlayerDeath;
            BarricadeManager.onDamageBarricadeRequested += OnDamageBarricadeRequested;
            BarricadeManager.onOpenStorageRequested += OnOpenStorageRequested;
            UnturnedPlayerEvents.OnPlayerUpdateGesture += OnPlayerUpdateGesture;
            StartCoroutine(CheckPlayerProximity());
            Rocket.Core.Logging.Logger.Log("KashiLootBox yüklendi!");
        }

        protected override void Unload()
        {
            Instance = null;
            UnturnedPlayerEvents.OnPlayerDeath -= OnPlayerDeath;
            BarricadeManager.onDamageBarricadeRequested -= OnDamageBarricadeRequested;
            BarricadeManager.onOpenStorageRequested -= OnOpenStorageRequested;
            UnturnedPlayerEvents.OnPlayerUpdateGesture -= OnPlayerUpdateGesture;
            StopAllCoroutines();
            Rocket.Core.Logging.Logger.Log("KashiLootBox kaldırıldı!");
        }

        private IEnumerator CheckPlayerProximity()
        {
            while (true)
            {
                yield return new WaitForSeconds(1);

                foreach (var player in Provider.clients)
                {
                    UnturnedPlayer unturnedPlayer = UnturnedPlayer.FromSteamPlayer(player);
                    CheckProximity(unturnedPlayer);
                }
            }
        }

        private void CheckProximity(UnturnedPlayer player)
        {
            var lootBoxes = new List<Transform>();
            foreach (var barricadeRegion in BarricadeManager.regions)
            {
                foreach (var drop in barricadeRegion.drops)
                {
                    if (drop.asset.id == LootBoxID)
                    {
                        lootBoxes.Add(drop.model);
                    }
                }
            }

            bool effectApplied = false;

            foreach (var lootBox in lootBoxes)
            {
                if (Vector3.Distance(player.Position, lootBox.position) <= Configuration.Instance.EffectRadius)
                {
                    if (!activeEffects.ContainsKey(player.CSteamID))
                    {
                        EffectManager.sendUIEffect(EffectID, (short)EffectID, player.CSteamID, true);
                        activeEffects[player.CSteamID] = true;
                    }
                    effectApplied = true;
                    break;
                }
            }

            if (!effectApplied && activeEffects.ContainsKey(player.CSteamID))
            {
                EffectManager.askEffectClearByID(EffectID, player.CSteamID);
                activeEffects.Remove(player.CSteamID);
            }
        }

        private void OnPlayerDeath(UnturnedPlayer deadPlayer, EDeathCause cause, ELimb limb, CSteamID killerId)
        {
            UnturnedPlayer killerPlayer = UnturnedPlayer.FromCSteamID(killerId);
            if (killerPlayer != null)
            {
                CreateLootBox(deadPlayer, killerPlayer);
                MarkDeathLocationOnMap(deadPlayer);
                UnturnedChat.Say(killerPlayer, Configuration.Instance.PlayerKilledMessage, Color.red);
                UnturnedChat.Say(deadPlayer, Configuration.Instance.PlayerDiedMessage, Color.red);
                Rocket.Core.Logging.Logger.Log("Oyuncu ölüm olayı tetiklendi.");
            }
            else
            {
                Rocket.Core.Logging.Logger.Log("Öldüren oyuncu null.");
            }
        }

        private void CreateLootBox(UnturnedPlayer deadPlayer, UnturnedPlayer killerPlayer)
        {
            if (deadPlayer.Inventory == null)
            {
                Rocket.Core.Logging.Logger.Log("Ölen oyuncunun envanteri null.");
                return;
            }

            Rocket.Core.Logging.Logger.Log("Loot kutusu oluşturuluyor...");

            var position = deadPlayer.Position;
            var rotation = Quaternion.Euler(0, deadPlayer.Player.transform.rotation.eulerAngles.y, 0);
            ulong ownerID = killerPlayer.CSteamID.m_SteamID;

            Transform transform = BarricadeManager.dropNonPlantedBarricade(new Barricade(LootBoxID), position, rotation, ownerID, 0);

            if (transform == null)
            {
                Rocket.Core.Logging.Logger.Log("Loot kutusu oluşturulamadı.");
                return;
            }

            InteractableStorage storage = transform.GetComponent<InteractableStorage>();

            if (storage == null)
            {
                Rocket.Core.Logging.Logger.Log("InteractableStorage bileşeni bulunamadı.");
                return;
            }

            storage.items.resize(storage.items.width, storage.items.height);

            var itemsToDrop = new List<ItemJar>();

            for (byte page = 0; page < PlayerInventory.PAGES; page++)
            {
                var pageItems = deadPlayer.Inventory.items[page];
                if (pageItems == null)
                {
                    Rocket.Core.Logging.Logger.Log($"Envanter sayfası {page} null.");
                    continue;
                }

                for (byte i = 0; i < pageItems.getItemCount(); i++)
                {
                    var itemJar = pageItems.getItem(i);
                    if (itemJar == null || itemJar.item == null)
                    {
                        Rocket.Core.Logging.Logger.Log("Envanterdeki eşya null.");
                        continue;
                    }
                    itemsToDrop.Add(itemJar);
                }
            }

            for (byte page = 0; page < PlayerInventory.PAGES; page++)
            {
                var pageItems = deadPlayer.Inventory.items[page];
                if (pageItems != null)
                {
                    pageItems.clear();
                }
            }

            foreach (var itemJar in itemsToDrop)
            {
                storage.items.tryAddItem(itemJar.item);
            }

            Rocket.Core.Logging.Logger.Log("Loot kutusu oyuncunun pozisyonunda oluşturuldu.");
            StartCoroutine(DespawnLootBox(transform, DespawnTime));
        }

        private IEnumerator DespawnLootBox(Transform transform, float delay)
        {
            yield return new WaitForSeconds(delay);

            var storage = transform.GetComponent<InteractableStorage>();
            if (storage != null)
            {
                storage.items.clear();
            }

            byte despawnX, despawnY;
            ushort despawnPlant, despawnDropIndex;
            BarricadeRegion despawnRegion;
            if (BarricadeManager.tryGetInfo(transform, out despawnX, out despawnY, out despawnPlant, out despawnDropIndex, out despawnRegion))
            {
                BarricadeManager.destroyBarricade(despawnRegion, despawnX, despawnY, despawnPlant, despawnDropIndex);
                Rocket.Core.Logging.Logger.Log("Loot kutusu zaman aşımına uğradı ve yok edildi.");
            }
        }

        private void MarkDeathLocationOnMap(UnturnedPlayer deadPlayer)
        {
            var position = deadPlayer.Position;
            deadPlayer.Player.quests.replicateSetMarker(true, position);
            Rocket.Core.Logging.Logger.Log("Ölüm yeri haritada işaretlendi.");
        }

        private Transform GetBarricadeUnderPlayerLook(UnturnedPlayer player)
        {
            Ray ray = new Ray(player.Player.look.aim.position, player.Player.look.aim.forward);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, 5f, RayMasks.BARRICADE_INTERACT))
            {
                return hit.transform;
            }
            return null;
        }

        private void HandleLootBoxCollection(UnturnedPlayer player, Transform transform)
        {
            if (transform == null)
                return;

            byte collectionX, collectionY;
            ushort collectionPlant, collectionDropIndex;
            BarricadeRegion collectionRegion;
            if (!BarricadeManager.tryGetInfo(transform, out collectionX, out collectionY, out collectionPlant, out collectionDropIndex, out collectionRegion))
            {
                Rocket.Core.Logging.Logger.Log("Barricade bilgileri alınamadı.");
                return;
            }

            BarricadeDrop drop = collectionRegion.drops[collectionDropIndex];
            if (drop == null || drop.asset.id != LootBoxID)
                return;

            Rocket.Core.Logging.Logger.Log("Loot kutusu bir oyuncu tarafından toplandı.");
            UnturnedChat.Say(player, Configuration.Instance.LootBoxDroppedMessage, Color.red);

            var storage = transform.GetComponent<InteractableStorage>();
            if (storage != null)
            {
                List<ItemJar> itemsToTransfer = new List<ItemJar>();
                for (byte i = 0; i < storage.items.getItemCount(); i++)
                {
                    var itemJar = storage.items.getItem(i);
                    if (itemJar != null)
                    {
                        itemsToTransfer.Add(itemJar);
                    }
                }

                foreach (var itemJar in itemsToTransfer)
                {
                    if (!player.Inventory.tryAddItem(itemJar.item, true))
                    {
                        ItemManager.dropItem(itemJar.item, player.Position, false, true, true);
                    }
                    storage.items.removeItem((byte)storage.items.items.IndexOf(itemJar));
                }
            }

            BarricadeManager.destroyBarricade(collectionRegion, collectionX, collectionY, collectionPlant, collectionDropIndex);
        }

        private void OnDamageBarricadeRequested(CSteamID instigatorSteamID, Transform transform, ref ushort pendingTotalDamage, ref bool shouldAllow, EDamageOrigin damageOrigin)
        {
            if (transform == null)
                return;

            byte damageX, damageY;
            ushort damagePlant, damageDropIndex;
            BarricadeRegion damageRegion;
            if (!BarricadeManager.tryGetInfo(transform, out damageX, out damageY, out damagePlant, out damageDropIndex, out damageRegion))
            {
                Rocket.Core.Logging.Logger.Log("Barricade bilgileri alınamadı.");
                return;
            }

            BarricadeDrop drop = damageRegion.drops[damageDropIndex];
            if (drop == null || drop.asset.id != LootBoxID)
                return;

            pendingTotalDamage = 65000; 
            shouldAllow = true;
            UnturnedPlayer player = UnturnedPlayer.FromCSteamID(instigatorSteamID);
            HandleLootBoxCollection(player, transform);
        }

        private void OnOpenStorageRequested(CSteamID steamID, InteractableStorage storage, ref bool shouldAllow)
        {
            if (storage == null || storage.transform == null)
            {
                shouldAllow = true;
                return;
            }

            byte openX, openY;
            ushort openPlant, openDropIndex;
            BarricadeRegion openRegion;
            if (!BarricadeManager.tryGetInfo(storage.transform, out openX, out openY, out openPlant, out openDropIndex, out openRegion))
            {
                shouldAllow = true;
                return;
            }

            BarricadeDrop drop = openRegion.drops[openDropIndex];
            if (drop == null || drop.asset == null)
            {
                shouldAllow = true;
                return;
            }

            if (drop.asset.id != LootBoxID)
            {
                shouldAllow = true;
                return;
            }

            UnturnedPlayer player = UnturnedPlayer.FromCSteamID(steamID);
            UnturnedChat.Say(player, Configuration.Instance.OpenAttemptMessage, Color.red);
            shouldAllow = false;

            HandleLootBoxCollection(player, storage.transform);
        }

        private void OnPlayerUpdateGesture(UnturnedPlayer player, UnturnedPlayerEvents.PlayerGesture gesture)
        {
            if (gesture == UnturnedPlayerEvents.PlayerGesture.Point)
            {
                var transform = GetBarricadeUnderPlayerLook(player);

                if (transform != null)
                {
                    HandleLootBoxCollection(player, transform);
                }
            }
        }

        public override TranslationList DefaultTranslations => new TranslationList()
        {
            { "lootbox_dropped", "İçinde eşyalar olan loot kutusu düştü. Eşyaları toplamak için F'ye bas." },
            { "open_attempt", "Kutuya bakarak point emotesi yapın." },
            { "point_gesture", "Kutuyu kırmak için point emotesi yapın." },
            { "inventory_full", "Envanteriniz dolu, bazı eşyalar yere düştü!" }
        };
    }
}
