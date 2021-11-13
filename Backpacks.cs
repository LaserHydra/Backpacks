﻿using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// TODO: Re-implement visual backpack
// TODO: Try to simulate a dropped item container as item container sourceEntity to customize the loot text.

namespace Oxide.Plugins
{
    [Info("Backpacks", "LaserHydra", "3.6.3")]
    [Description("Allows players to have a Backpack which provides them extra inventory space.")]
    internal class Backpacks : RustPlugin
    {
        #region Fields

        private const ushort MinSize = 1;
        private const ushort MaxSize = 7;
        private const ushort SlotsPerRow = 6;
        private const string GUIPanelName = "BackpacksUI";

        private const string UsagePermission = "backpacks.use";
        private const string GUIPermission = "backpacks.gui";
        private const string FetchPermission = "backpacks.fetch";
        private const string AdminPermission = "backpacks.admin";
        private const string KeepOnDeathPermission = "backpacks.keepondeath";
        private const string KeepOnWipePermission = "backpacks.keeponwipe";
        private const string NoBlacklistPermission = "backpacks.noblacklist";

        private const string CoffinPrefab = "assets/prefabs/misc/halloween/coffin/coffinstorage.prefab";
        private const string BackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
        private const string ResizableLootPanelName = "generic_resizable";

        private readonly BackpackManager _backpackManager = new BackpackManager();
        private Dictionary<string, ushort> _backpackSizePermissions = new Dictionary<string, ushort>();

        private readonly DynamicHookSubscriber<BasePlayer> _backpackLooters = new DynamicHookSubscriber<BasePlayer>(
            nameof(OnLootEntityEnd)
        );

        private ProtectionProperties _immortalProtection;
        private string _cachedUI;

        private static Backpacks _instance;
        private Configuration _config;
        private StoredData _storedData;

        [PluginReference]
        private Plugin Arena, EventManager;

        #endregion

        #region Hooks

        private void Init()
        {
            _instance = this;

            permission.RegisterPermission(UsagePermission, this);
            permission.RegisterPermission(GUIPermission, this);
            permission.RegisterPermission(FetchPermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(KeepOnDeathPermission, this);
            permission.RegisterPermission(KeepOnWipePermission, this);
            permission.RegisterPermission(NoBlacklistPermission, this);

            for (ushort size = MinSize; size <= MaxSize; size++)
            {
                var sizePermission = $"{UsagePermission}.{size}";
                permission.RegisterPermission(sizePermission, this);
                _backpackSizePermissions.Add(sizePermission, size);
            }

            _backpackSizePermissions = _backpackSizePermissions
                .OrderByDescending(kvp => kvp.Value)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            _storedData = StoredData.Load();

            _backpackLooters.UnsubscribeAll();

            Unsubscribe(nameof(OnPlayerSleep));
            Unsubscribe(nameof(OnPlayerSleepEnded));
        }

        private void OnServerInitialized()
        {
            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "BackpacksProtection";
            _immortalProtection.Add(1);

            foreach (var player in BasePlayer.activePlayerList)
                CreateGUI(player);

            Subscribe(nameof(OnPlayerSleep));
            Subscribe(nameof(OnPlayerSleepEnded));
        }

        private void Unload()
        {
            UnityEngine.Object.Destroy(_immortalProtection);

            _storedData.Save();
            _backpackManager.SaveAndKillCachedBackpacks();

            foreach (var player in BasePlayer.activePlayerList)
                DestroyGUI(player);

            _instance = null;
        }

        private void OnNewSave(string filename)
        {
            // Ensure config is loaded
            LoadConfig();

            if (!_config.ClearBackpacksOnWipe)
                return;

            _backpackManager.ClearCache();

            IEnumerable<string> fileNames = Interface.Oxide.DataFileSystem.GetFiles(Name)
                .Select(fn => {
                    return fn.Split(Path.DirectorySeparatorChar).Last()
                        .Replace(".json", string.Empty);
                });

            int skippedBackpacks = 0;

            foreach (var fileName in fileNames)
            {
                ulong userId;

                if (!ulong.TryParse(fileName, out userId))
                    continue;

                if (permission.UserHasPermission(fileName, KeepOnWipePermission))
                {
                    skippedBackpacks++;
                    continue;
                }

                var backpack = new Backpack(userId);
                backpack.SaveData(ignoreDirty: true);
            }

            string skippedBackpacksMessage = skippedBackpacks > 0 ? $", except {skippedBackpacks} due to being exempt" : string.Empty;
            PrintWarning($"New save created. All backpacks were cleared{skippedBackpacksMessage}. Players with the '{KeepOnWipePermission}' permission are exempt. Clearing backpacks can be disabled for all players in the configuration file.");
        }

        private void OnServerSave()
        {
            _storedData.Save();

            if (_config.SaveBackpacksOnServerSave)
            {
                _backpackManager.SaveAndKillCachedBackpacks();
                _backpackManager.ClearCache();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            var backpack = _backpackManager.GetCachedBackpack(player.userID);
            if (backpack == null)
                return;

            if (!_config.SaveBackpacksOnServerSave)
            {
                backpack.SaveAndKill();
                _backpackManager.RemoveFromCache(backpack);
            }
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer storageContainer)
        {
            var backpack = _backpackManager.GetCachedBackpackForContainer(storageContainer.inventory);
            if (backpack == null)
                return;

            _backpackManager.OnBackpackClosed(backpack, player);
        }

        // Handle player death by normal means.
        private void OnEntityDeath(BasePlayer player, HitInfo info) =>
            OnEntityKill(player);

        // Handle player death while sleeping in a safe zone.
        private void OnEntityKill(BasePlayer player)
        {
            if (player.IsNpc)
                return;

            DestroyGUI(player);

            if (!_backpackManager.HasBackpackFile(player.userID)
                || permission.UserHasPermission(player.UserIDString, KeepOnDeathPermission))
                return;

            if (_config.EraseOnDeath)
                _backpackManager.EraseContents(player.userID);
            else if (_config.DropOnDeath)
                _backpackManager.Drop(player.userID, player.transform.position);
        }

        private void OnGroupPermissionGranted(string group, string perm)
        {
            if (perm.Equals(GUIPermission))
            {
                foreach (IPlayer player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, group)))
                {
                    CreateGUI(player.Object as BasePlayer);
                }
            }
        }

        private void OnGroupPermissionRevoked(string group, string perm)
        {
            if (perm.Equals(GUIPermission))
            {
                foreach (IPlayer player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, group)))
                {
                    if (!permission.UserHasPermission(player.Id, GUIPermission))
                    {
                        DestroyGUI(player.Object as BasePlayer);
                    }
                }
            }
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (perm.Equals(GUIPermission))
                CreateGUI(BasePlayer.Find(userId));
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            if (perm.Equals(GUIPermission) && !permission.UserHasPermission(userId, GUIPermission))
                DestroyGUI(BasePlayer.Find(userId));
        }

        private void OnPlayerConnected(BasePlayer player) => CreateGUI(player);

        private void OnPlayerRespawned(BasePlayer player) => CreateGUI(player);

        private void OnPlayerSleepEnded(BasePlayer player) => CreateGUI(player);

        private void OnPlayerSleep(BasePlayer player) => DestroyGUI(player);

        #endregion

        #region API

        private Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
        {
            return _backpackManager.GetAllCachedContainers();
        }

        private void API_EraseBackpack(ulong userId)
        {
            _backpackManager.TryEraseForPlayer(userId);
        }

        private DroppedItemContainer API_DropBackpack(BasePlayer player)
        {
            var backpack = _backpackManager.GetBackpackIfExists(player.userID);
            if (backpack == null)
                return null;

            return _backpackManager.Drop(player.userID, player.transform.position);
        }

        #endregion

        #region Commands

        [ChatCommand("backpack")]
        private void OpenBackpackChatCommand(BasePlayer player, string cmd, string[] args)
        {
            if (!player.CanInteract())
                return;

            if (!permission.UserHasPermission(player.UserIDString, UsagePermission))
            {
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
                return;
            }

            player.EndLooting();

            // Must delay opening in case the chat is still closing or the loot panel may close instantly.
            timer.Once(0.5f, () => _backpackManager.OpenBackpack(player.userID, player));
        }

        [ConsoleCommand("backpack.open")]
        private void OpenBackpackConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (player == null || !player.CanInteract())
                return;

            if (!permission.UserHasPermission(player.UserIDString, UsagePermission))
            {
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
                return;
            }

            var lootingContainer = player.inventory.loot.containers.FirstOrDefault();
            if (lootingContainer != null && _backpackManager.GetCachedBackpackForContainer(lootingContainer) != null)
            {
                player.EndLooting();
                // HACK: Send empty respawn information to fully close the player inventory (toggle backpack closed)
                player.ClientRPCPlayer(null, player, "OnRespawnInformation");
                return;
            }

            var wasLooting = player.inventory.loot.IsLooting();
            if (wasLooting)
            {
                player.EndLooting();
                player.inventory.loot.SendImmediate();
            }

            // Need a short delay when looting so the client doesn't reuse the previously drawn generic_resizable loot panel.
            var delaySeconds = wasLooting
                ? 0.1f
                // Key binds automatically pass the "True" argument at the end.
                // Can open instantly since not looting and chat is assumed to be closed.
                : arg.Args?.LastOrDefault() == "True"
                ? 0f
                // Not opening via key bind, so the chat window may be open.
                // Must delay in case the chat is still closing or else the loot panel may close instantly.
                : 0.1f;

            timer.Once(delaySeconds, () => _backpackManager.OpenBackpack(player.userID, player));
        }

        [ConsoleCommand("backpack.fetch")]
        private void FetchBackpackItemConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (player == null || !player.CanInteract())
                return;

            if (!permission.UserHasPermission(player.UserIDString, FetchPermission))
            {
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
                return;
            }

            if (!arg.HasArgs(2))
            {
                PrintToConsole(player, lang.GetMessage("Backpack Fetch Syntax", this, player.UserIDString));
                return;
            }

            if (!VerifyCanOpenBackpack(player, player.userID))
                return;

            string[] args = arg.Args;
            string itemArg = args[0];
            int itemID;

            ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemArg);
            if (itemDefinition != null)
            {
                itemID = itemDefinition.itemid;
            }
            else
            {
                // User may have provided an itemID instead of item short name
                if (!int.TryParse(itemArg, out itemID))
                {
                    PrintToChat(player, lang.GetMessage("Invalid Item", this, player.UserIDString));
                    return;
                }

                itemDefinition = ItemManager.FindItemDefinition(itemID);

                if (itemDefinition == null)
                {
                    PrintToChat(player, lang.GetMessage("Invalid Item", this, player.UserIDString));
                    return;
                }
            }

            int desiredAmount;
            if (!int.TryParse(args[1], out desiredAmount))
            {
                PrintToChat(player, lang.GetMessage("Invalid Item Amount", this, player.UserIDString));
                return;
            }

            if (desiredAmount < 1)
            {
                PrintToChat(player, lang.GetMessage("Invalid Item Amount", this, player.UserIDString));
                return;
            }

            string itemLocalizedName = itemDefinition.displayName.translated;
            Backpack backpack = _backpackManager.GetBackpack(player.userID);
            backpack.EnsureContainer();

            int quantityInBackpack = backpack.GetItemQuantity(itemID);

            if (quantityInBackpack == 0)
            {
                PrintToChat(player, lang.GetMessage("Item Not In Backpack", this, player.UserIDString), itemLocalizedName);
                return;
            }

            if (desiredAmount > quantityInBackpack)
                desiredAmount = quantityInBackpack;

            int amountTransferred = backpack.MoveItemsToPlayerInventory(player, itemID, desiredAmount);

            if (amountTransferred > 0)
            {
                PrintToChat(player, lang.GetMessage("Items Fetched", this, player.UserIDString), amountTransferred, itemLocalizedName);
            }
            else
            {
                PrintToChat(player, lang.GetMessage("Fetch Failed", this, player.UserIDString), itemLocalizedName);
            }
        }

        [ConsoleCommand("backpack.erase")]
        private void EraseBackpackServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null)
            {
                // Only allowed as a server command.
                return;
            }

            var args = arg.Args;

            ulong userId;
            if (!arg.HasArgs(1) || !ulong.TryParse(args[0], out userId))
            {
                PrintWarning($"Syntax: {arg.cmd.FullName} <id>");
                return;
            }

            if (_backpackManager.TryEraseForPlayer(userId))
                PrintWarning($"Erased backpack for player {userId}.");
            else
                PrintWarning($"Player {userId} has no backpack to erase.");
        }

        [ChatCommand("viewbackpack")]
        private void ViewBackpack(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
                return;
            }

            if (args.Length != 1)
            {
                PrintToChat(player, lang.GetMessage("View Backpack Syntax", this, player.UserIDString));
                return;
            }

            string failureMessage;
            IPlayer targetPlayer = FindPlayer(args[0], out failureMessage);

            if (targetPlayer == null)
            {
                PrintToChat(player, failureMessage);
                return;
            }

            BasePlayer targetBasePlayer = targetPlayer.Object as BasePlayer;
            ulong backpackOwnerId = targetBasePlayer?.userID ?? ulong.Parse(targetPlayer.Id);

            timer.Once(0.5f, () => _backpackManager.OpenBackpack(backpackOwnerId, player));
        }

        [ChatCommand("backpackgui")]
        private void ToggleBackpackGUI(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, GUIPermission))
            {
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
                return;
            }

            if (_storedData.PlayersWithDisabledGUI.Contains(player.userID))
            {
                _storedData.PlayersWithDisabledGUI.Remove(player.userID);
                CreateGUI(player);
            }
            else
            {
                _storedData.PlayersWithDisabledGUI.Add(player.userID);
                DestroyGUI(player);
            }

            PrintToChat(player, lang.GetMessage("Toggled Backpack GUI", this, player.UserIDString));
        }

        #endregion

        #region Helper Methods

        private IPlayer FindPlayer(string nameOrID, out string failureMessage)
        {
            failureMessage = string.Empty;

            ulong userId;
            if (nameOrID.StartsWith("7656119") && nameOrID.Length == 17 && ulong.TryParse(nameOrID, out userId))
            {
                IPlayer player = covalence.Players.All.FirstOrDefault(p => p.Id == nameOrID);

                if (player == null)
                    failureMessage = string.Format(lang.GetMessage("User ID not Found", this), nameOrID);

                return player;
            }

            var foundPlayers = new List<IPlayer>();

            foreach (IPlayer player in covalence.Players.All)
            {
                if (player.Name.Equals(nameOrID, StringComparison.InvariantCultureIgnoreCase))
                    return player;

                if (player.Name.ToLower().Contains(nameOrID.ToLower()))
                    foundPlayers.Add(player);
            }

            switch (foundPlayers.Count)
            {
                case 0:
                    failureMessage = string.Format(lang.GetMessage("User Name not Found", this), nameOrID);
                    return null;

                case 1:
                    return foundPlayers[0];

                default:
                    string names = string.Join(", ", foundPlayers.Select(p => p.Name).ToArray());
                    failureMessage = string.Format(lang.GetMessage("Multiple Players Found", this), names);
                    return null;
            }
        }

        private bool VerifyCanOpenBackpack(BasePlayer looter, ulong ownerId)
        {
            if (IsPlayingEvent(looter))
            {
                PrintToChat(looter, lang.GetMessage("May Not Open Backpack In Event", this, looter.UserIDString));
                return false;
            }

            var hookResult = Interface.Oxide.CallHook("CanOpenBackpack", looter, ownerId);
            if (hookResult != null && hookResult is string)
            {
                _instance.PrintToChat(looter, hookResult as string);
                return false;
            }

            return true;
        }

        private bool IsPlayingEvent(BasePlayer player)
        {
            // Multiple event/arena plugins define the isEventPlayer method as a standard.
            var isPlaying = Interface.Call("isEventPlayer", player);
            if (isPlaying is bool && (bool)isPlaying)
                return true;

            if (EventManager != null)
            {
                // EventManager 3.x
                isPlaying = EventManager.Call("isPlaying", player);
                if (isPlaying is bool && (bool)isPlaying)
                    return true;
            }

            if (Arena != null)
            {
                isPlaying = Arena.Call("IsEventPlayer", player);
                if (isPlaying is bool && (bool)isPlaying)
                    return true;
            }

            return false;
        }

        private void CreateGUI(BasePlayer player)
        {
            if (player == null || player.IsNpc || !player.IsAlive() || player.IsSleeping())
                return;

            if (!permission.UserHasPermission(player.UserIDString, GUIPermission))
                return;

            if (_storedData.PlayersWithDisabledGUI.Contains(player.userID))
                return;

            CuiHelper.DestroyUi(player, GUIPanelName);

            if (_cachedUI == null)
            {
                var elements = new CuiElementContainer();
                var BackpacksUIPanel = elements.Add(new CuiPanel
                {
                    Image = { Color = _instance._config.GUI.Color },
                    RectTransform = {
                        AnchorMin = _config.GUI.GUIButtonPosition.AnchorsMin,
                        AnchorMax = _config.GUI.GUIButtonPosition.AnchorsMax,
                        OffsetMin = _config.GUI.GUIButtonPosition.OffsetsMin,
                        OffsetMax = _config.GUI.GUIButtonPosition.OffsetsMax
                    },
                    CursorEnabled = false
                }, "Overlay", GUIPanelName);

                elements.Add(new CuiElement
                {
                    Parent = GUIPanelName,
                    Components = {
                        new CuiRawImageComponent { Url = _instance._config.GUI.Image },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });

                elements.Add(new CuiButton
                {
                    Button = { Command = "backpack.open", Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = "" }
                }, BackpacksUIPanel);

                _cachedUI = CuiHelper.ToJson(elements);
            }

            CuiHelper.AddUi(player, _cachedUI);
        }

        private void DestroyGUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, GUIPanelName);
        }

        private static void TerminateEntityOnClient(BaseEntity entity, Connection connection)
        {
            if (Net.sv.write.Start())
            {
                Net.sv.write.PacketID(Message.Type.EntityDestroy);
                Net.sv.write.EntityID(entity.net.ID);
                Net.sv.write.UInt8((byte)BaseNetworkable.DestroyMode.None);
                Net.sv.write.Send(new SendInfo(connection));
            }
        }

        private static void LoadData<T>(out T data, string filename) =>
            data = Interface.Oxide.DataFileSystem.ReadObject<T>(filename);

        private static void SaveData<T>(T data, string filename) =>
            Interface.Oxide.DataFileSystem.WriteObject(filename, data);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["No Permission"] = "You don't have permission to use this command.",
                ["May Not Open Backpack In Event"] = "You may not open a backpack while participating in an event!",
                ["View Backpack Syntax"] = "Syntax: /viewbackpack <name or id>",
                ["User ID not Found"] = "Could not find player with ID '{0}'",
                ["User Name not Found"] = "Could not find player with name '{0}'",
                ["Multiple Players Found"] = "Multiple matching players found:\n{0}",
                ["Backpack Over Capacity"] = "Your backpack was over capacity. Overflowing items were added to your inventory or dropped.",
                ["Blacklisted Items Removed"] = "Your backpack contained blacklisted items. They have been added to your inventory or dropped.",
                ["Backpack Fetch Syntax"] = "Syntax: backpack.fetch <item short name or id> <amount>",
                ["Invalid Item"] = "Invalid Item Name or ID.",
                ["Invalid Item Amount"] = "Item amount must be an integer greater than 0.",
                ["Item Not In Backpack"] = "Item \"{0}\" not found in backpack.",
                ["Items Fetched"] = "Fetched {0} \"{1}\" from backpack.",
                ["Fetch Failed"] = "Couldn't fetch \"{0}\" from backpack. Inventory may be full.",
                ["Toggled Backpack GUI"] = "Toggled backpack GUI button.",
            }, this);
        }

        #endregion

        #region Configuration

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration
            {
                BlacklistedItems = new HashSet<string>
                {
                    "autoturret",
                    "lmg.m249"
                }
            };

            SaveConfig();
        }

        private class Configuration
        {
            private ushort _backpackSize = 1;

            [JsonProperty("Backpack Size (1-7 Rows)")]
            public ushort BackpackSize
            {
                get { return _backpackSize; }
                set { _backpackSize = (ushort) Mathf.Clamp(value, MinSize, MaxSize); }
            }

            [JsonProperty("Drop on Death (true/false)")]
            public bool DropOnDeath = true;

            [JsonProperty("Erase on Death (true/false)")]
            public bool EraseOnDeath = false;

            [JsonProperty("Clear Backpacks on Map-Wipe (true/false)")]
            public bool ClearBackpacksOnWipe = false;

            [JsonProperty("Only Save Backpacks on Server-Save (true/false)")]
            public bool SaveBackpacksOnServerSave = false;

            [JsonProperty("Use Blacklist (true/false)")]
            public bool UseBlacklist = false;

            [JsonProperty("Blacklisted Items (Item Shortnames)")]
            public HashSet<string> BlacklistedItems = new HashSet<string>();

            [JsonProperty("Use Whitelist (true/false)")]
            public bool UseWhitelist = false;

            [JsonProperty("Whitelisted Items (Item Shortnames)")]
            public HashSet<string> WhitelistedItems = new HashSet<string>();

            [JsonProperty("Minimum Despawn Time (Seconds)")]
            public float MinimumDespawnTime = 300;

            [JsonProperty("GUI Button")]
            public GUIButton GUI = new GUIButton();

            [JsonProperty("Softcore")]
            public SoftcoreOptions Softcore = new SoftcoreOptions();

            public class GUIButton
            {
                [JsonProperty(PropertyName = "Image")]
                public string Image = "https://i.imgur.com/CyF0QNV.png";

                [JsonProperty(PropertyName = "Background color (RGBA format)")]
                public string Color = "1 0.96 0.88 0.15";

                [JsonProperty(PropertyName = "GUI Button Position")]
                public Position GUIButtonPosition = new Position();
                public class Position
                {
                    [JsonProperty(PropertyName = "Anchors Min")]
                    public string AnchorsMin = "0.5 0.0";

                    [JsonProperty(PropertyName = "Anchors Max")]
                    public string AnchorsMax = "0.5 0.0";

                    [JsonProperty(PropertyName = "Offsets Min")]
                    public string OffsetsMin = "185 18";

                    [JsonProperty(PropertyName = "Offsets Max")]
                    public string OffsetsMax = "245 78";
                }
            }

            public class SoftcoreOptions
            {
                [JsonProperty("Reclaim Fraction")]
                public float ReclaimFraction = 0.5f;
            }

            [JsonIgnore]
            public bool ItemRestrictionEnabled => UseWhitelist || UseBlacklist;

            public bool IsRestrictedItem(Item item)
            {
                if (UseWhitelist)
                    return !WhitelistedItems.Contains(item.info.shortname);

                if (UseBlacklist)
                    return BlacklistedItems.Contains(item.info.shortname);

                return false;
            }
        }

        #endregion

        #region Stored Data

        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(_instance.Name);
                if (data == null)
                {
                    _instance.PrintWarning($"Data file {_instance.Name}.json is invalid. Creating new data file.");
                    data = new StoredData();
                    data.Save();
                }
                return data;
            }

            [JsonProperty("PlayersWithDisabledGUI")]
            public HashSet<ulong> PlayersWithDisabledGUI = new HashSet<ulong>();

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(_instance.Name, this);
        }

        #endregion

        #region Dynamic Hook Subscriber

        private class DynamicHookSubscriber<T>
        {
            private HashSet<T> _list = new HashSet<T>();
            private string[] _hookNames;

            public DynamicHookSubscriber(params string[] hookNames)
            {
                _hookNames = hookNames;
            }

            public void Add(T item)
            {
                if (_list.Add(item) && _list.Count == 1)
                    SubscribeAll();
            }

            public void Remove(T item)
            {
                if (_list.Remove(item) && _list.Count == 0)
                    UnsubscribeAll();
            }

            public void SubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _instance.Subscribe(hookName);
            }

            public void UnsubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _instance.Unsubscribe(hookName);
            }
        }

        #endregion

        #region Backpack Manager

        private class BackpackManager
        {
            private readonly Dictionary<ulong, Backpack> _backpacks = new Dictionary<ulong, Backpack>();
            private readonly Dictionary<ItemContainer, Backpack> _backpackContainers = new Dictionary<ItemContainer, Backpack>();

            private static string GetBackpackPath(ulong userId) => $"{_instance.Name}/{userId}";

            // Data migration from v2.x.x to v3.x.x
            private static bool TryMigrateData(string fileName)
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(fileName))
                {
                    return false;
                }

                Dictionary<string, object> data;
                LoadData(out data, fileName);
                if (data == null)
                    return false;

                if (data.ContainsKey("ownerID") && data.ContainsKey("Inventory"))
                {
                    var inventory = (JObject) data["Inventory"];

                    data["OwnerID"] = data["ownerID"];
                    data["Items"] = inventory.Value<object>("Items");

                    data.Remove("ownerID");
                    data.Remove("Inventory");
                    data.Remove("Size");

                    SaveData(data, fileName);

                    return true;
                }

                return false;
            }

            public bool HasBackpackFile(ulong userId)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(GetBackpackPath(userId));
            }

            public Backpack GetCachedBackpack(ulong userId)
            {
                Backpack backpack;
                return _backpacks.TryGetValue(userId, out backpack)
                    ? backpack
                    : null;
            }

            public Backpack GetBackpack(ulong userId)
            {
                var backpack = GetCachedBackpack(userId);
                if (backpack != null)
                    return backpack;

                return Load(userId);
            }

            public Backpack GetBackpackIfExists(ulong userId)
            {
                return HasBackpackFile(userId)
                    ? GetBackpack(userId)
                    : null;
            }

            public void RegisterContainer(ItemContainer container, Backpack backpack)
            {
                _backpackContainers[container] = backpack;
            }

            public void UnregisterContainer(ItemContainer container)
            {
                _backpackContainers.Remove(container);
            }

            public Backpack GetCachedBackpackForContainer(ItemContainer container)
            {
                Backpack backpack;
                return _backpackContainers.TryGetValue(container, out backpack)
                    ? backpack
                    : null;
            }

            public Dictionary<ulong, ItemContainer> GetAllCachedContainers()
            {
                var cachedContainersByUserId = new Dictionary<ulong, ItemContainer>();

                foreach (var entry in _backpacks)
                {
                    var container = entry.Value.GetContainer();
                    if (container != null)
                        cachedContainersByUserId[entry.Key] = container;
                }

                return cachedContainersByUserId;
            }

            public void EraseContents(ulong userId)
            {
                GetBackpackIfExists(userId)?.EraseContents();
            }

            public DroppedItemContainer Drop(ulong userId, Vector3 position)
            {
                return GetBackpackIfExists(userId)?.Drop(position);
            }

            public void OpenBackpack(ulong backpackOwnerId, BasePlayer looter)
            {
                _instance._backpackLooters.Add(looter);
                GetBackpack(backpackOwnerId).Open(looter);
            }

            public void OnBackpackClosed(Backpack backpack, BasePlayer looter)
            {
                _instance._backpackLooters.Remove(looter);
                backpack.OnClosed(looter);
				Interface.CallHook("OnBackpackClosed", looter, backpack.OwnerId, backpack.GetContainer());

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    backpack.SaveData();
                }
            }

            private Backpack Load(ulong userId)
            {
                var fileName = GetBackpackPath(userId);

                TryMigrateData(fileName);

                Backpack backpack;
                LoadData(out backpack, fileName);

                if (backpack == null)
                {
                    // Sometimes backpack data files can become corrupt, which will be represented as null.
                    // When that happens, simply create a new one.
                    backpack = new Backpack(userId);
                }

                // Ensure the backpack always has an owner id.
                // This improves compatibility with plugins such as Wipe Data Cleaner which reset the file to `{}`.
                backpack.OwnerId = userId;

                _backpacks[userId] = backpack;

                return backpack;
            }

            public bool TryEraseForPlayer(ulong userId)
            {
                var backpack = GetBackpackIfExists(userId);
                if (backpack == null)
                    return false;

                backpack.EraseContents(force: true);
                return true;
            }

            public void SaveAndKillCachedBackpacks()
            {
                foreach (var backpack in _backpacks.Values)
                {
                    backpack.SaveAndKill();
                }
            }

            public void RemoveFromCache(Backpack backpack)
            {
                _backpacks.Remove(backpack.OwnerId);
            }

            public void ClearCache()
            {
                _backpacks.Clear();
            }
        }

        #endregion

        #region Backpack

        private class NoRagdollCollision : FacepunchBehaviour
        {
            private Collider _collider;

            private void Awake()
            {
                _collider = GetComponent<Collider>();
            }

            private void OnCollisionEnter(Collision collision)
            {
                if (collision.collider.IsOnLayer(Rust.Layer.Ragdoll))
                {
                    Physics.IgnoreCollision(_collider, collision.collider);
                }
            }
        }

        private class Backpack
        {
            private StorageContainer _storageContainer;
            private ItemContainer _itemContainer;
            private List<BasePlayer> _looters = new List<BasePlayer>();
            private BasePlayer _lastLooter;
            private bool _dirty = false;

            [JsonIgnore]
            public bool ProcessedRestrictedItems { get; private set; }

            [JsonIgnore]
            public string OwnerIdString { get; private set; }

            [JsonProperty("OwnerID")]
            public ulong OwnerId { get; set; }

            [JsonProperty("Items")]
            private List<ItemData> _itemDataCollection = new List<ItemData>();

            public Backpack(ulong ownerId) : base()
            {
                OwnerId = ownerId;
                OwnerIdString = OwnerId.ToString();
            }

            ~Backpack()
            {
                KillContainer();
            }

            public IPlayer FindOwnerPlayer() => _instance.covalence.Players.FindPlayerById(OwnerIdString);

            public bool IsUnderlyingContainer(ItemContainer itemContainer) => _itemContainer == itemContainer;

            public ushort GetAllowedSize()
            {
                foreach(var kvp in _instance._backpackSizePermissions)
                {
                    if (_instance.permission.UserHasPermission(OwnerIdString, kvp.Key))
                        return kvp.Value;
                }

                return _instance._config.BackpackSize;
            }

            public ItemContainer GetContainer() => _itemContainer;

            private int GetAllowedCapacity() => GetAllowedSize() * SlotsPerRow;

            private StorageContainer SpawnStorageContainer(int capacity)
            {
                var storageEntity = GameManager.server.CreateEntity(CoffinPrefab, new Vector3(0, -1000, 0));
                if (storageEntity == null)
                    return null;

                var containerEntity = storageEntity as StorageContainer;
                if (containerEntity == null)
                {
                    UnityEngine.Object.Destroy(storageEntity);
                    return null;
                }

                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<DestroyOnGroundMissing>());
                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<GroundWatch>());

                foreach (var collider in containerEntity.GetComponentsInChildren<Collider>())
                    UnityEngine.Object.DestroyImmediate(collider);

                containerEntity.baseProtection = _instance._immortalProtection;
                containerEntity.panelName = ResizableLootPanelName;

                // Make sure the container does not try to sync position, or else it may determine that it's outside of the world bounds and destroy itself.
                containerEntity.syncPosition = false;

                containerEntity.limitNetworking = true;
                containerEntity.EnableSaving(false);
                containerEntity.Spawn();

                containerEntity.inventory.allowedContents = ItemContainer.ContentsType.Generic;
                containerEntity.inventory.capacity = capacity;

                return containerEntity;
            }

            private int GetHighestUsedSlot()
            {
                var highestUsedSlot = -1;
                foreach (var itemData in _itemDataCollection)
                {
                    if (itemData.Position > highestUsedSlot)
                        highestUsedSlot = itemData.Position;
                }
                return highestUsedSlot;
            }

            private bool ShouldAcceptItem(Item item)
            {
                // Skip checking restricted items if they haven't been processed, to avoid erasing them.
                // Restricted items will be dropped when the owner opens the backpack.
                if (_instance._config.ItemRestrictionEnabled
                    && ProcessedRestrictedItems
                    && !_instance.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission)
                    && _instance._config.IsRestrictedItem(item))
                {
                    return false;
                }

                object hookResult = Interface.CallHook("CanBackpackAcceptItem", OwnerId, _itemContainer, item);
                if (hookResult is bool && (bool)hookResult == false)
                    return false;

                return true;
            }

            private bool CanAcceptItem(Item item, int amount)
            {
                // Explicitly track hook time so server owners can be informed of the cost.
                _instance.TrackStart();
                var result = ShouldAcceptItem(item);
                _instance.TrackEnd();
                return result;
            }

            private void MarkDirty () => _dirty = true;

            public void EnsureContainer()
            {
                if (_storageContainer != null)
                    return;

                _storageContainer = SpawnStorageContainer(GetAllowedCapacity());
                _itemContainer = _storageContainer.inventory;

                _instance._backpackManager.RegisterContainer(_itemContainer, this);

                if (GetHighestUsedSlot() >= _itemContainer.capacity)
                {
                    // Temporarily increase the capacity to allow all items to fit
                    // Extra items will be addressed when the backpack is opened by the owner
                    // If an admin views the backpack in the meantime, it will appear as max capacity
                    _itemContainer.capacity = MaxSize * SlotsPerRow;
                }

                foreach (var backpackItem in _itemDataCollection)
                {
                    var item = backpackItem.ToItem();
                    if (item != null)
                    {
                        if (!item.MoveToContainer(_itemContainer, item.position))
                            item.Remove();
                    }
                }

                // Apply the item filter only after filling the container initially.
                // This avoids unnecessary CanBackpackAcceptItem hooks calls on initial creation.
                _itemContainer.canAcceptItem += this.CanAcceptItem;
                _itemContainer.onDirty += this.MarkDirty;
            }

            private void TerminateContainerForLastLooter()
            {
                if (_storageContainer == null || _lastLooter == null || _lastLooter.Connection == null)
                    return;

                TerminateEntityOnClient(_storageContainer, _lastLooter.Connection);
            }

            public void KillContainer()
            {
                ForceCloseAllLooters();
                TerminateContainerForLastLooter();

                if (_storageContainer == null || _storageContainer.IsDestroyed)
                    return;

                _instance._backpackManager.UnregisterContainer(_itemContainer);
                _storageContainer.Kill();
            }

            public void SaveAndKill()
            {
                SaveData();
                KillContainer();
            }

            public void Open(BasePlayer looter)
            {
                if (!_instance.VerifyCanOpenBackpack(looter, OwnerId))
                    return;

                EnsureContainer();

                // Only drop items when the owner is opening the backpack.
                if (looter.userID == OwnerId)
                {
                    MaybeRemoveRestrictedItems(looter);
                    MaybeAdjustCapacityAndHandleOverflow(looter);
                }

                if (!_looters.Contains(looter))
                    _looters.Add(looter);

                if (_lastLooter != looter)
                {
                    // There's one edge case with this, which is that if two players are looting a backpack,
                    // the container will be destroyed for the previous looter (only possible if one of the players is admin).
                    // This can be improved in the future, but it's very minor impact,
                    // as it only disallows right-clicking items into the backpack until reopened.
                    TerminateContainerForLastLooter();

                    // The client must be sent a snapshot of the container entity in order to use right-click to move items into it.
                    _storageContainer.SendAsSnapshot(looter.Connection);

                    // We track the last looter so that we can explicitly terminate the container later.
                    // If we don't terminate the container on the client at some point,
                    // then containers could potentially build up on clients (if reloading the plugin often).
                    _lastLooter = looter;
                }

                _storageContainer.PlayerOpenLoot(looter, _storageContainer.panelName, doPositionChecks: false);

                Interface.CallHook("OnBackpackOpened", looter, OwnerId, _storageContainer.inventory);
            }

            private void MaybeAdjustCapacityAndHandleOverflow(BasePlayer receiver)
            {
                var allowedCapacity = GetAllowedCapacity();

                if (_itemContainer.capacity <= allowedCapacity)
                {
                    // Increasing or maintaining capacity is always safe to do
                    _itemContainer.capacity = allowedCapacity;
                    return;
                }

                // Close for all looters since we are going to alter the capacity
                ForceCloseAllLooters();

                // Item order is preserved so that compaction is more deterministic
                // Basically, items earlier in the backpack are more likely to stay in the backpack
                var extraItems = _itemContainer.itemList
                    .OrderBy(item => item.position)
                    .Where(item => item.position >= allowedCapacity)
                    .ToArray();

                // Remove the extra items from the container so the capacity can be reduced
                foreach (var item in extraItems)
                {
                    item.RemoveFromContainer();
                }

                // Capacity must be reduced before attempting to move overflowing items or they will be placed in the extra slots
                _itemContainer.capacity = allowedCapacity;

                var itemsDroppedOrGivenToPlayer = 0;
                foreach (var item in extraItems)
                {
                    // Try to move the item to a vacant backpack slot or add to an existing stack in the backpack
                    // If the item cannot be completely compacted into the backpack, the remainder is given to the player
                    // If the item does not completely fit in the player inventory, the remainder is automatically dropped
                    if (!item.MoveToContainer(_itemContainer))
                    {
                        itemsDroppedOrGivenToPlayer++;
                        receiver.GiveItem(item);
                    }
                }

                if (itemsDroppedOrGivenToPlayer > 0)
                {
                    _instance.PrintToChat(receiver, _instance.lang.GetMessage("Backpack Over Capacity", _instance, receiver.UserIDString));
                }
            }

            private void MaybeRemoveRestrictedItems(BasePlayer receiver)
            {
                if (!_instance._config.ItemRestrictionEnabled)
                    return;

                // Optimization: Avoid processing item restrictions every time the backpack is opened.
                if (ProcessedRestrictedItems)
                    return;

                if (_instance.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission))
                {
                    // Don't process item restrictions while the player has the noblacklist permission.
                    // Setting this flag allows the item restrictions to be processed again in case the noblacklist permission is revoked.
                    ProcessedRestrictedItems = false;
                    return;
                }

                var itemsDroppedOrGivenToPlayer = 0;
                for (var i = _itemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = _itemContainer.itemList[i];
                    if (_instance._config.IsRestrictedItem(item))
                    {
                        itemsDroppedOrGivenToPlayer++;
                        item.RemoveFromContainer();
                        receiver.GiveItem(item);
                    }
                }

                if (itemsDroppedOrGivenToPlayer > 0)
                {
                    _instance.PrintToChat(receiver, _instance.lang.GetMessage("Blacklisted Items Removed", _instance, receiver.UserIDString));
                }

                ProcessedRestrictedItems = true;
            }

            public void ForceCloseAllLooters()
            {
                if (_looters.Count == 0)
                    return;

                foreach (BasePlayer looter in _looters.ToArray())
                {
                    ForceCloseLooter(looter);
                }
            }

            public void OnClosed(BasePlayer looter)
            {
                _looters.Remove(looter);
            }

            private void ForceCloseLooter(BasePlayer looter)
            {
                looter.inventory.loot.Clear();
                looter.inventory.loot.MarkDirty();
                looter.inventory.loot.SendImmediate();

                OnClosed(looter);
            }

            public DroppedItemContainer Drop(Vector3 position)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                if (_storageContainer == null && _itemDataCollection.Count == 0)
                    return null;

                object hookResult = Interface.CallHook("CanDropBackpack", OwnerId, position);

                if (hookResult is bool && (bool)hookResult == false)
                    return null;

                EnsureContainer();
                ForceCloseAllLooters();
                ReclaimItemsForSoftcore();

                // Check again since the items may have all been reclaimed for Softcore.
                if (_itemContainer.itemList.Count == 0)
                    return null;

                DroppedItemContainer container = GameManager.server.CreateEntity(BackpackPrefab, position, Quaternion.identity) as DroppedItemContainer;

                container.gameObject.AddComponent<NoRagdollCollision>();

                container.lootPanelName = ResizableLootPanelName;
                container.playerName = $"{FindOwnerPlayer()?.Name ?? "Somebody"}'s Backpack";
                container.playerSteamID = OwnerId;

                container.inventory = new ItemContainer();
                container.inventory.ServerInitialize(null, _itemContainer.itemList.Count);
                container.inventory.GiveUID();
                container.inventory.entityOwner = container;
                container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

                foreach (Item item in _itemContainer.itemList.ToArray())
                {
                    if (!item.MoveToContainer(container.inventory))
                    {
                        item.RemoveFromContainer();
                        item.Remove();
                    }
                }

                container.Spawn();
                container.ResetRemovalTime(Math.Max(_instance._config.MinimumDespawnTime, container.CalculateRemovalTime()));

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }

                return container;
            }

            private void ReclaimItemsForSoftcore()
            {
                var softcoreGameMode = BaseGameMode.svActiveGameMode as GameModeSoftcore;
                if (softcoreGameMode == null || ReclaimManager.instance == null)
                    return;

                List<Item> reclaimItemList = Facepunch.Pool.GetList<Item>();
                softcoreGameMode.AddFractionOfContainer(_itemContainer, ref reclaimItemList, _instance._config.Softcore.ReclaimFraction);
                if (reclaimItemList.Count > 0)
                {
                    // There's a vanilla bug where accessing the reclaim backpack will erase items in the reclaim entry above 32.
                    // So we just add a new reclaim entry which can only be accessed at the terminal to avoid this issue.
                    ReclaimManager.instance.AddPlayerReclaim(OwnerId, reclaimItemList);
                }
                Facepunch.Pool.FreeList(ref reclaimItemList);
            }

            public void EraseContents(bool force = false)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                if (_storageContainer == null && _itemDataCollection.Count == 0)
                    return;

                if (!force)
                {
                    object hookResult = Interface.CallHook("CanEraseBackpack", OwnerId);

                    if (hookResult is bool && (bool)hookResult == false)
                        return;
                }

                if (_itemContainer != null)
                {
                    ForceCloseAllLooters();

                    foreach (var item in _itemContainer.itemList.ToList())
                    {
                        item.RemoveFromContainer();
                        item.Remove();
                    }
                }
                else
                {
                    // Optimization: Simply clear the data when there is no container.
                    _itemDataCollection.Clear();
                }

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }
            }

            public void SaveData(bool ignoreDirty = false)
            {
                if (!ignoreDirty && !_dirty)
                    return;

                // There is possibly no container if wiping a backpack on server wipe.
                if (_itemContainer != null)
                {
                    _itemDataCollection = _itemContainer.itemList
                        .Select(ItemData.FromItem)
                        .ToList();
                }

                Backpacks.SaveData(this, $"{_instance.Name}/{OwnerId}");
                _dirty = false;
            }

            public int GetItemQuantity(int itemID) => _itemContainer.FindItemsByItemID(itemID).Sum(item => item.amount);

            public int MoveItemsToPlayerInventory(BasePlayer player, int itemID, int desiredAmount)
            {
                List<Item> matchingItemStacks = _itemContainer.FindItemsByItemID(itemID);
                int amountTransferred = 0;

                foreach (Item itemStack in matchingItemStacks)
                {
                    int remainingDesiredAmount = desiredAmount - amountTransferred;
                    Item itemToTransfer = (itemStack.amount > remainingDesiredAmount) ? itemStack.SplitItem(remainingDesiredAmount) : itemStack;
                    int initialStackAmount = itemToTransfer.amount;

                    bool transferFullySucceeded = player.inventory.GiveItem(itemToTransfer);
                    amountTransferred += initialStackAmount;

                    if (!transferFullySucceeded)
                    {
                        int amountRemainingInStack = itemToTransfer.amount;

                        // Decrement the amountTransferred by the amount remaining in the stack
                        // Since earlier we incremented it by the full stack amount
                        amountTransferred -= amountRemainingInStack;

                        if (itemToTransfer != itemStack)
                        {
                            // Add the remaining items from the split stack back to the original stack
                            itemStack.amount += amountRemainingInStack;
                            itemStack.MarkDirty();
                        }
                        break;
                    }

                    if (amountTransferred >= desiredAmount)
                        break;
                }

                if (amountTransferred > 0 && !_instance._config.SaveBackpacksOnServerSave)
                    SaveData();

                return amountTransferred;
            }
        }

        public class ItemData
        {
            public int ID;
            public int Position = -1;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Amount;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool IsBlueprint;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int BlueprintTarget;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Fuel;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int FlameFuel;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Condition;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float MaxCondition = -1;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Ammo;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int AmmoType;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Text;

            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<ItemData> Contents = new List<ItemData>();

            public Item ToItem()
            {
                if (Amount == 0)
                    return null;

                Item item = ItemManager.CreateByItemID(ID, Amount, Skin);
                if (item == null)
                    return null;

                item.position = Position;

                if (IsBlueprint)
                {
                    item.blueprintTarget = BlueprintTarget;
                    return item;
                }

                item.fuel = Fuel;
                item.condition = Condition;

                if (MaxCondition != -1)
                    item.maxCondition = MaxCondition;

                if (Contents != null)
                {
                    if (Contents.Count > 0)
                    {
                        if (item.contents == null)
                        {
                            item.contents = new ItemContainer();
                            item.contents.ServerInitialize(null, Contents.Count);
                            item.contents.GiveUID();
                            item.contents.parent = item;
                        }
                        foreach (var contentItem in Contents)
                            contentItem.ToItem()?.MoveToContainer(item.contents);
                    }
                }
                else
                    item.contents = null;

                BaseProjectile.Magazine magazine = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine;
                FlameThrower flameThrower = item.GetHeldEntity()?.GetComponent<FlameThrower>();

                if (magazine != null)
                {
                    magazine.contents = Ammo;
                    magazine.ammoType = ItemManager.FindItemDefinition(AmmoType);
                }

                if (flameThrower != null)
                    flameThrower.ammo = FlameFuel;

                if (DataInt > 0)
                {
                    item.instanceData = new ProtoBuf.Item.InstanceData
                    {
                        ShouldPool = false,
                        dataInt = DataInt
                    };
                }

                item.text = Text;

                if (Name != null)
                    item.name = Name;

                return item;
            }

            public static ItemData FromItem(Item item) => new ItemData
            {
                ID = item.info.itemid,
                Position = item.position,
                Ammo = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.contents ?? 0,
                AmmoType = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.ammoType?.itemid ?? 0,
                Amount = item.amount,
                Condition = item.condition,
                MaxCondition = item.maxCondition,
                Fuel = item.fuel,
                Skin = item.skin,
                Contents = item.contents?.itemList?.Select(FromItem).ToList(),
                FlameFuel = item.GetHeldEntity()?.GetComponent<FlameThrower>()?.ammo ?? 0,
                IsBlueprint = item.IsBlueprint(),
                BlueprintTarget = item.blueprintTarget,
                DataInt = item.instanceData?.dataInt ?? 0,
                Name = item.name,
                Text = item.text
            };
        }

        #endregion
    }
}