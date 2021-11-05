// #define DEBUG

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

        private const string BackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
        private const string ResizableLootPanelName = "generic_resizable";

        private readonly Dictionary<ulong, Backpack> _backpacks = new Dictionary<ulong, Backpack>();
        private readonly Dictionary<BasePlayer, Backpack> _openBackpacks = new Dictionary<BasePlayer, Backpack>();
        private readonly Dictionary<ulong, DroppedItemContainer> _lastDroppedBackpacks = new Dictionary<ulong, DroppedItemContainer>();
        private Dictionary<string, ushort> _backpackSizePermissions = new Dictionary<string, ushort>();

        private static Backpacks _instance;

        private Configuration _config;
        private StoredData _storedData;

        [PluginReference]
        private Plugin Arena, EventManager;

        #endregion

        #region Hooks

        private void Init()
        {
            Unsubscribe(nameof(OnPlayerSleep));
            Unsubscribe(nameof(OnPlayerSleepEnded));
        }

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnPlayerSleep));
            Subscribe(nameof(OnPlayerSleepEnded));
        }

        private void Loaded()
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

            if (!_config.DropOnDeath || !ConVar.Server.corpses)
            {
                Unsubscribe(nameof(OnPlayerCorpseSpawned));
            }

            _storedData = StoredData.Load();

            foreach (var player in BasePlayer.activePlayerList)
                CreateGUI(player);
        }

        private void Unload()
        {
            _storedData.Save();

            foreach (var backpack in _backpacks.Values)
            {
                backpack.ForceCloseAllLooters();
                backpack.SaveData();
                backpack.KillContainer();
            }

            foreach (var player in BasePlayer.activePlayerList)
                DestroyGUI(player);
        }

        private void OnNewSave(string filename)
        {
            // Ensure config is loaded
            LoadConfig();

            if (!_config.ClearBackpacksOnWipe)
                return;

            _backpacks.Clear();

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
                backpack.SaveData();
            }

            string skippedBackpacksMessage = skippedBackpacks > 0 ? $", except {skippedBackpacks} due to being exempt" : string.Empty;
            PrintWarning($"New save created. All backpacks were cleared{skippedBackpacksMessage}. Players with the '{KeepOnWipePermission}' permission are exempt. Clearing backpacks can be disabled for all players in the configuration file.");
        }

        private void OnServerSave()
        {
            _storedData.Save();

            if (_config.SaveBackpacksOnServerSave)
            {
                foreach (var backpack in _backpacks.Values)
                {
                    backpack.ForceCloseAllLooters();
                    backpack.SaveData();
                    backpack.KillContainer();
                }

                _backpacks.Clear();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (_openBackpacks.ContainsKey(player))
                _openBackpacks[player].OnClose(player);

            if (!_config.SaveBackpacksOnServerSave && _backpacks.ContainsKey(player.userID))
            {
                var backpack = _backpacks[player.userID];

                backpack.ForceCloseAllLooters();
                backpack.SaveData();
                backpack.KillContainer();

                _backpacks.Remove(player.userID);
            }
        }

        private object CanLootPlayer(BasePlayer looted, BasePlayer looter)
        {
            if (_openBackpacks.ContainsKey(looter)
                && (looter == looted || permission.UserHasPermission(looter.UserIDString, AdminPermission)))
            {
                return true;
            }

            return null;
        }

        private object CanAcceptItem(ItemContainer container, Item item)
        {
            Backpack backpack = Backpack.GetForContainer(container);
            if (backpack == null)
                return null;

            // Prevent erasing items that have since become restricted.
            // Restricted items will be dropped when the owner opens the backpack.
            if (!backpack.Initialized)
                return null;

            if (_config.ItemRestrictionEnabled
                && !permission.UserHasPermission(backpack.OwnerIdString, NoBlacklistPermission)
                && _config.IsRestrictedItem(item))
            {
                return ItemContainer.CanAcceptResult.CannotAccept;
            }

            object hookResult = Interface.CallHook("CanBackpackAcceptItem", backpack.OwnerId, container, item);
            if (hookResult is bool && (bool)hookResult == false)
                return ItemContainer.CanAcceptResult.CannotAccept;

            return null;
        }

        private void OnPlayerLootEnd(PlayerLoot playerLoot)
        {
            var player = (BasePlayer) playerLoot.gameObject.ToBaseEntity();

            if (_openBackpacks.ContainsKey(player))
            {
                _openBackpacks[player].OnClose(player);
            }
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

            if (!Backpack.HasBackpackFile(player.userID)
                || permission.UserHasPermission(player.UserIDString, KeepOnDeathPermission))
                return;

            var backpack = Backpack.Get(player.userID);
            backpack.ForceCloseAllLooters();

            if (_config.EraseOnDeath)
                backpack.EraseContents();
            else if (_config.DropOnDeath)
                DropBackpackWithReducedCorpseCollision(backpack, player.transform.position);
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, BaseCorpse corpse)
        {
            if (!_lastDroppedBackpacks.ContainsKey(player.userID))
                return;

            var container = _lastDroppedBackpacks[player.userID];
            if (container == null)
                return;

            var corpseCollider = corpse.GetComponent<Collider>();
            var containerCollider = _lastDroppedBackpacks[player.userID].GetComponent<Collider>();

            if (corpseCollider != null && containerCollider != null)
                Physics.IgnoreCollision(corpseCollider, containerCollider);

            _lastDroppedBackpacks.Remove(player.userID);
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

        #region Commands

#if DEBUG
        [ChatCommand("c")]
        private void RunContainerDebugCommand(BasePlayer player)
        {
            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit))
            {
                var entity = hit.GetEntity();

                if (entity != null)
                {
                    var storageContainer = entity.GetComponent<StorageContainer>();

                    if (storageContainer != null)
                    {
                        var data = new Dictionary<string, object>
                        {
                            ["panelName"] = storageContainer.panelName,
                            ["uid"] = storageContainer.inventory.uid,
                            ["isServer"] = storageContainer.inventory.isServer,
                            ["capacity"] = storageContainer.inventory.capacity,
                            ["maxStackSize"] = storageContainer.inventory.maxStackSize,
                            ["entityOwner"] = storageContainer.inventory.entityOwner,
                            ["playerOwner"] = storageContainer.inventory.playerOwner,
                            ["flags"] = storageContainer.inventory.flags,
                            ["allowedContents"] = storageContainer.inventory.allowedContents,
                            ["availableSlots"] = storageContainer.inventory.availableSlots.Count
                        };

                        PrintToChat(string.Join(Environment.NewLine, data.Select(kvp => $"[{kvp.Key}] : {kvp.Value}").ToArray()));

                        timer.In(0.5f, () => PlayerLootContainer(player, storageContainer.inventory));
                    }

                    else
                        PrintToChat("not a storage container");
                }
            }
        }
#endif

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
            timer.Once(0.5f, () => Backpack.Get(player.userID).Open(player));
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

            if (_openBackpacks.ContainsKey(player))
            {
                player.EndLooting();
                // HACK: Send empty respawn information to fully close the player inventory (toggle backpack closed)
                player.ClientRPCPlayer(null, player, "OnRespawnInformation");
                return;
            }

            if (player.inventory.loot.IsLooting())
            {
                player.EndLooting();
                player.inventory.loot.SendImmediate();
            }

            // Key binds automatically pass the "True" argument at the end.
            if (arg.HasArgs(1) && arg.Args[0] == "True")
            {
                // Open instantly when using a key bind.
                timer.Once(0.05f, () => Backpack.Get(player.userID).Open(player));
            }
            else
            {
                // Not opening via key bind, so the chat window may be open.
                // Must delay opening in case the chat is still closing or the loot panel may close instantly.
                timer.Once(0.1f, () => Backpack.Get(player.userID).Open(player));
            }
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
            Backpack backpack = Backpack.Get(player.userID);
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

            if (Backpack.TryEraseForPlayer(userId))
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
            ulong id = targetBasePlayer?.userID ?? ulong.Parse(targetPlayer.Id);

            Backpack backpack = Backpack.Get(id);
            timer.Once(0.5f, () => backpack.Open(player));
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

        private static void PlayerLootContainer(BasePlayer player, ItemContainer container)
        {
            player.inventory.loot.Clear();
            player.inventory.loot.PositionChecks = false;
            player.inventory.loot.entitySource = container.entityOwner ?? player;
            player.inventory.loot.itemSource = null;
            player.inventory.loot.MarkDirty();
            player.inventory.loot.AddContainer(container);
            player.inventory.loot.SendImmediate();

            player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", ResizableLootPanelName);
        }

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

        private DroppedItemContainer DropBackpackWithReducedCorpseCollision(Backpack backpack, Vector3 position)
        {
            var droppedContainer = backpack.Drop(position);

            if (droppedContainer != null && ConVar.Server.corpses)
            {
                if (_lastDroppedBackpacks.ContainsKey(backpack.OwnerId))
                    _lastDroppedBackpacks[backpack.OwnerId] = droppedContainer;
                else
                    _lastDroppedBackpacks.Add(backpack.OwnerId, droppedContainer);
            }

            return droppedContainer;
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

            CuiHelper.AddUi(player, elements);
        }

        private void DestroyGUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, GUIPanelName);
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

        #region Backpack

        #region API

        private Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
        {
            return _backpacks.ToDictionary(x => x.Key, x => x.Value.GetContainer());
        }

        private void API_EraseBackpack(ulong userId)
        {
            Backpack.TryEraseForPlayer(userId);
        }

        private DroppedItemContainer API_DropBackpack(BasePlayer player)
        {
            if (!Backpack.HasBackpackFile(player.userID))
                return null;

            var backpack = Backpack.Get(player.userID);
            backpack.ForceCloseAllLooters();

            return DropBackpackWithReducedCorpseCollision(backpack, player.transform.position);
        }

        #endregion

        private class Backpack
        {
            private ItemContainer _itemContainer = new ItemContainer();
            private List<BasePlayer> _looters = new List<BasePlayer>();

            private bool _processedRestrictedItems = false;

            [JsonIgnore]
            public bool Initialized { get; private set; } = false;

            [JsonIgnore]
            public string OwnerIdString { get; private set; }

            [JsonProperty("OwnerID")]
            public ulong OwnerId { get; private set; }

            [JsonProperty("Items")]
            private List<ItemData> _itemDataCollection = new List<ItemData>();

            public Backpack(ulong ownerId) : base()
            {
                OwnerId = ownerId;
            }

            ~Backpack()
            {
                ForceCloseAllLooters();
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

            public void Initialize()
            {
                if (!Initialized)
                {
                    OwnerIdString = OwnerId.ToString();
                }
                else
                {
                    // Force-close since we are re-initializing
                    ForceCloseAllLooters();
                }

                var ownerPlayer = FindOwnerPlayer()?.Object as BasePlayer;
                _itemContainer.entityOwner = ownerPlayer;

                if (Initialized)
                    return;

                _itemContainer.isServer = true;
                _itemContainer.allowedContents = ItemContainer.ContentsType.Generic;
                _itemContainer.GiveUID();
                _itemContainer.capacity = GetAllowedCapacity();

                if (_itemDataCollection.Count != 0 && _itemDataCollection.Max(item => item.Position) >= _itemContainer.capacity)
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
                        item.MoveToContainer(_itemContainer, item.position);
                    }
                }

                Initialized = true;
            }

            public void KillContainer()
            {
                Initialized = false;

                _itemContainer.Kill();
                _itemContainer = null;

                ItemManager.DoRemoves();
            }

            public void Open(BasePlayer looter)
            {
                if (_instance._openBackpacks.ContainsKey(looter))
                    return;

                _instance._openBackpacks.Add(looter, this);

                if (!Initialized)
                {
                    Initialize();
                }

                // The entityOwner may no longer be valid if it was instantiated while the player was offline (due to player death)
                if (_itemContainer.entityOwner == null)
                {
                    var ownerPlayer = FindOwnerPlayer()?.Object as BasePlayer;
                    if (ownerPlayer != null)
                        _itemContainer.entityOwner = ownerPlayer;
                }

                // Container can't be looted for some reason.
                // We should cancel here and remove the looter from the open backpacks again.
                if (looter.inventory.loot.IsLooting()
                    || !(_itemContainer.entityOwner?.CanBeLooted(looter) ?? looter.CanBeLooted(looter)))
                {
                    _instance._openBackpacks.Remove(looter);
                    return;
                }

                if (!_instance.VerifyCanOpenBackpack(looter, OwnerId))
                {
                    _instance._openBackpacks.Remove(looter);
                    return;
                }

                // Only drop items when the owner is opening the backpack.
                if (looter.userID == OwnerId)
                {
                    MaybeRemoveRestrictedItems(looter);
                    MaybeAdjustCapacityAndHandleOverflow(looter);
                }

                if (!_looters.Contains(looter))
                    _looters.Add(looter);

                PlayerLootContainer(looter, _itemContainer);

                Interface.CallHook("OnBackpackOpened", looter, OwnerId, _itemContainer);
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
                if (_processedRestrictedItems)
                    return;

                if (_instance.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission))
                {
                    // Don't process item restrictions while the player has the noblacklist permission.
                    // Setting this flag allows the item restrictions to be processed again in case the noblacklist permission is revoked.
                    _processedRestrictedItems = false;
                    return;
                }

                _processedRestrictedItems = true;

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
            }

            public void ForceCloseAllLooters()
            {
                foreach (BasePlayer looter in _looters.ToArray())
                {
                    ForceCloseLooter(looter);
                }
            }

            private void ForceCloseLooter(BasePlayer looter)
            {
                looter.inventory.loot.Clear();
                looter.inventory.loot.MarkDirty();
                looter.inventory.loot.SendImmediate();

                OnClose(looter);
            }

            public void OnClose(BasePlayer looter)
            {
                _looters.Remove(looter);
                _instance._openBackpacks.Remove(looter);

				Interface.CallHook("OnBackpackClosed", looter, OwnerId, _itemContainer);

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }
            }

            public DroppedItemContainer Drop(Vector3 position)
            {
                object hookResult = Interface.CallHook("CanDropBackpack", OwnerId, position);

                if (hookResult is bool && (bool)hookResult == false)
                    return null;

                if (_itemContainer.itemList.Count == 0)
                    return null;

                ReclaimItemsForSoftcore();

                // Check again since the items may have all been reclaimed for Softcore.
                if (_itemContainer.itemList.Count == 0)
                    return null;

                BaseEntity entity = GameManager.server.CreateEntity(BackpackPrefab, position, Quaternion.identity);
                DroppedItemContainer container = entity as DroppedItemContainer;

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
                        item.Remove();
                        item.DoRemove();
                    }
                }

                container.Spawn();
                container.ResetRemovalTime(Math.Max(_instance._config.MinimumDespawnTime, container.CalculateRemovalTime()));

                ItemManager.DoRemoves();

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
                if (!force)
                {
                    object hookResult = Interface.CallHook("CanEraseBackpack", OwnerId);

                    if (hookResult is bool && (bool)hookResult == false)
                        return;
                }

                foreach (var item in _itemContainer.itemList.ToList())
                {
                    item.Remove();
                    item.DoRemove();
                }

                ItemManager.DoRemoves();

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }
            }

            public void SaveData()
            {
                _itemDataCollection = _itemContainer.itemList
                    .Select(ItemData.FromItem)
                    .ToList();

                Backpacks.SaveData(this, $"{_instance.Name}/{OwnerId}");
            }

            public void Cache() =>
                _instance._backpacks[OwnerId] = this;

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

            public static bool HasBackpackFile(ulong id)
            {
                var fileName = $"{_instance.Name}/{id}";

                return Interface.Oxide.DataFileSystem.ExistsDatafile(fileName);
            }

            public static Backpack GetForContainer(ItemContainer container)
            {
                foreach (var backpack in _instance._backpacks.Values)
                {
                    if (backpack.IsUnderlyingContainer(container))
                        return backpack;
                }
                return null;
            }

            private static Backpack GetCachedBackpack(ulong id)
            {
                Backpack backpack;
                return _instance._backpacks.TryGetValue(id, out backpack)
                    ? backpack
                    : null;
            }

            public static Backpack Get(ulong id)
            {
                if (id == 0)
                    _instance.PrintWarning("Accessing backpack for ID 0! Please report this to the author with as many details as possible.");

                var cachedBackpack = GetCachedBackpack(id);
                if (cachedBackpack != null)
                    return cachedBackpack;

                var fileName = $"{_instance.Name}/{id}";

                TryMigrateData(fileName);

                Backpack backpack;
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(fileName))
                {
                    LoadData(out backpack, fileName);
                    if (backpack == null)
                    {
                        _instance.PrintWarning($"Data file {fileName}.json is invalid. Creating new data file.");
                        backpack = new Backpack(id);
                        Backpacks.SaveData(backpack, fileName);
                    }

                    // Ensure the backpack has the correct owner id, even if it was removed from the data file.
                    backpack.OwnerId = id;
                }
                else
                {
                    backpack = new Backpack(id);
                    Backpacks.SaveData(backpack, fileName);
                }

                Interface.Oxide.DataFileSystem.GetDatafile(fileName).Settings = new JsonSerializerSettings
                {
                    DefaultValueHandling = DefaultValueHandling.Ignore
                };

                backpack.Cache();
                backpack.Initialize();

                return backpack;
            }

            public static bool TryEraseForPlayer(ulong id)
            {
                Backpack backpack = GetCachedBackpack(id);
                if (backpack != null)
                {
                    backpack.ForceCloseAllLooters();
                    backpack.EraseContents(force: true);
                    return true;
                }

                if (!HasBackpackFile(id))
                    return false;

                // If the backpack is not in the cache, create an empty one and add it.
                // In case it isn't saved immediately, this ensures it will be saved as empty on server save.
                backpack = new Backpack(id);
                backpack.Cache();

                if (!_instance._config.SaveBackpacksOnServerSave)
                    backpack.SaveData();

                return true;
            }
        }

        public class ItemData
        {
            public int ID;
            public int Position = -1;
            public int Amount;
            public bool IsBlueprint;
            public int BlueprintTarget;
            public ulong Skin;
            public float Fuel;
            public int FlameFuel;
            public float Condition;
            public float MaxCondition = -1;
            public int Ammo;
            public int AmmoType;
            public int DataInt;
            public string Name;
            public string Text;

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