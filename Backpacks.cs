// #define DEBUG

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// TODO: Re-implement visual backpack
// TODO: Try to simulate a dropped item container as item container sourceEntity to customize the loot text.

namespace Oxide.Plugins
{
    [Info("Backpacks", "LaserHydra", "3.2.1")]
    [Description("Allows players to have a Backpack which provides them extra inventory space.")]
    internal class Backpacks : RustPlugin
    {
        #region Fields

        private const ushort MinSize = 1;
        private const ushort MaxSize = 7;
        private const ushort SlotsPerRow = 6;

        private const string UsagePermission = "backpacks.use";
        private const string FetchPermission = "backpacks.fetch";
        private const string AdminPermission = "backpacks.admin";
        private const string KeepOnDeathPermission = "backpacks.keepondeath";

        private const string BackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";

        private readonly Dictionary<ulong, Backpack> _backpacks = new Dictionary<ulong, Backpack>();
        private readonly Dictionary<BasePlayer, Backpack> _openBackpacks = new Dictionary<BasePlayer, Backpack>();
        private readonly Dictionary<ulong, DroppedItemContainer> _lastDroppedBackpacks = new Dictionary<ulong, DroppedItemContainer>();
        private Dictionary<string, ushort> _backpackSizePermissions = new Dictionary<string, ushort>();

        private static Backpacks _instance;

        private Configuration _config;

        [PluginReference]
        private RustPlugin EventManager;

        #endregion

        #region Hooks

        private void Loaded()
        {
            _instance = this;

            permission.RegisterPermission(UsagePermission, this);
            permission.RegisterPermission(FetchPermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(KeepOnDeathPermission, this);

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
                Unsubscribe("OnPlayerCorpse");
            }
        }

        private void Unload()
        {
            foreach (var backpack in _backpacks.Values)
            {
                backpack.ForceCloseAllLooters();
                backpack.SaveData();
                backpack.KillContainer();
            }
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

            foreach (var fileName in fileNames)
            {
                ulong userId;

                if (!ulong.TryParse(fileName, out userId))
                    continue;

                var backpack = new Backpack(userId);
                backpack.SaveData();
            }

            PrintWarning("New save created. All backpacks were cleared. This can be disabled in the configuration file.");
        }

        private void OnServerSave()
        {
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
            if (!_config.UseBlacklist)
                return null;

            Backpack backpack = _backpacks.Values.FirstOrDefault(b => b.IsUnderlyingContainer(container));

            if (backpack != null)
            {
                // Is the Item blacklisted
                if (_config.BlacklistedItems.Any(shortName => shortName == item.info.shortname))
                    return ItemContainer.CanAcceptResult.CannotAccept;

                object hookResult = Interface.CallHook("CanBackpackAcceptItem", backpack.OwnerId, container, item);

                if (hookResult is bool && (bool)hookResult == false)
                    return ItemContainer.CanAcceptResult.CannotAccept;
            }

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

        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            if (victim is BasePlayer && !(victim is NPCPlayer) && !(victim is HTNPlayer))
            {
                var player = (BasePlayer) victim;

                if (Backpack.HasBackpackFile(player.userID))
                {
                    var backpack = Backpack.Get(player.userID);

                    backpack.ForceCloseAllLooters();

                    if (permission.UserHasPermission(player.UserIDString, KeepOnDeathPermission))
                        return;

                    if (_config.EraseOnDeath)
                        backpack.EraseContents();
                    else if (_config.DropOnDeath)
                    {
                        var droppedContainer = backpack.Drop(player.transform.position);
                        
                        if (droppedContainer != null && ConVar.Server.corpses)
                        {
                            if (_lastDroppedBackpacks.ContainsKey(player.userID))
                                _lastDroppedBackpacks[player.userID] = droppedContainer;
                            else
                                _lastDroppedBackpacks.Add(player.userID, droppedContainer);
                        }
                    }
                }
            }
        }

        private void OnPlayerCorpse(BasePlayer player, BaseCorpse corpse)
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
            if (perm.StartsWith(UsagePermission))
            {
                foreach (IPlayer player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, group)))
                {
                    OnUsagePermissionChanged(player.Id);
                }
            }
        }

        private void OnGroupPermissionRevoked(string group, string perm)
        {
            if (perm.StartsWith(UsagePermission))
            {
                foreach (IPlayer player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, group)))
                {
                    OnUsagePermissionChanged(player.Id);
                }
            }
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (perm.StartsWith(UsagePermission))
                OnUsagePermissionChanged(userId);
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            if (perm.StartsWith(UsagePermission))
                OnUsagePermissionChanged(userId);
        }

        private void OnUsagePermissionChanged(string userIdString)
        {
            if (permission.UserHasPermission(userIdString, UsagePermission))
            {
                var userId = ulong.Parse(userIdString);
                Backpack.Get(userId).Initialize();
            }
        }

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
            if (permission.UserHasPermission(player.UserIDString, UsagePermission))
                timer.Once(0.5f, () => Backpack.Get(player.userID).Open(player));
            else
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
        }

        [ConsoleCommand("backpack.open")]
        private void OpenBackpackConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (player == null)
                return;

            if (permission.UserHasPermission(player.UserIDString, UsagePermission))
            {
                player.EndLooting();
                timer.Once(0.1f, () => Backpack.Get(player.userID).Open(player));
            }
            else
                PrintToChat(player, lang.GetMessage("No Permission", this, player.UserIDString));
        }

        [ConsoleCommand("backpack.fetch")]
        private void FetchBackpackItemConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (player == null)
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

            player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", "genericlarge");
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
            if (EventManager?.Call<bool>("isPlaying", looter) ?? false)
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

        private static void LoadData<T>(out T data, string filename = null) => 
            data = Interface.Oxide.DataFileSystem.ReadObject<T>(filename ?? _instance.Name);

        private static void SaveData<T>(T data, string filename = null) => 
            Interface.Oxide.DataFileSystem.WriteObject(filename ?? _instance.Name, data);

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
                ["Backpack Fetch Syntax"] = "Syntax: backpack.fetch <item short name or id> <amount>",
                ["Invalid Item"] = "Invalid Item Name or ID.",
                ["Invalid Item Amount"] = "Item amount must be an integer greater than 0.",
                ["Item Not In Backpack"] = "Item \"{0}\" not found in backpack.",
                ["Items Fetched"] = "Fetched {0} \"{1}\" from backpack.",
                ["Fetch Failed"] = "Couldn't fetch \"{0}\" from backpack. Inventory may be full."
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

            [JsonProperty("Use Blacklist (true/false)")]
            public bool UseBlacklist = false;

            [JsonProperty("Clear Backpacks on Map-Wipe (true/false)")]
            public bool ClearBackpacksOnWipe = false;

            [JsonProperty("Only Save Backpacks on Server-Save (true/false)")]
            public bool SaveBackpacksOnServerSave = false;

            [JsonProperty("Blacklisted Items (Item Shortnames)")]
            public HashSet<string> BlacklistedItems;
        }

        #endregion

        #region Backpack

        private class Backpack
        {
            private bool _initialized = false;
            private string _ownerIdString;
            private bool _hasPossibleOverflow = false;

            private ItemContainer _itemContainer = new ItemContainer();
            private List<BasePlayer> _looters = new List<BasePlayer>();

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

            public IPlayer FindOwnerPlayer() => _instance.covalence.Players.FindPlayerById(_ownerIdString);

            public bool IsUnderlyingContainer(ItemContainer itemContainer) => _itemContainer == itemContainer;

            public ushort GetAllowedSize()
            {
                foreach(var kvp in _instance._backpackSizePermissions)
                {
                    if (_instance.permission.UserHasPermission(_ownerIdString, kvp.Key))
                        return kvp.Value;
                }

                return _instance._config.BackpackSize;
            }

            private int GetAllowedCapacity() => GetAllowedSize() * SlotsPerRow;

            public void Initialize()
            {
                if (!_initialized)
                {
                    _ownerIdString = OwnerId.ToString();
                }
                else
                {
                    // Force-close since we are re-initializing
                    ForceCloseAllLooters();
                }

                var ownerPlayer = FindOwnerPlayer()?.Object as BasePlayer;
                _itemContainer.entityOwner = ownerPlayer;

                var allowedCapacity = GetAllowedCapacity();
                if (!_initialized)
                {
                    _itemContainer.isServer = true;
                    _itemContainer.allowedContents = ItemContainer.ContentsType.Generic;
                    _itemContainer.GiveUID();
                    _itemContainer.capacity = allowedCapacity;

                    if (_itemDataCollection.Count != 0 && _itemDataCollection.Max(item => item.Position) >= allowedCapacity)
                    {
                        // Temporarily increase the capacity to allow all items to fit
                        // Extra items will be addressed when the backpack is opened by the owner
                        // If an admin views the backpack in the meantime, it will appear as max capacity
                        _itemContainer.capacity = MaxSize * SlotsPerRow;
                        _hasPossibleOverflow = true;
                    }

                    foreach (var backpackItem in _itemDataCollection)
                    {
                        var item = backpackItem.ToItem();

                        if (item != null)
                        {
                            item.MoveToContainer(_itemContainer, item.position);
                        }
                    }

                    _initialized = true;
                }
                else if (_itemContainer.capacity > allowedCapacity)
                {
                    // The capacity will be reduced later when the backpack is opened by the owner
                    _hasPossibleOverflow = true;
                }
                else if (_itemContainer.capacity < allowedCapacity)
                {
                    // Allowed capacity was increased since last initialized
                    _itemContainer.capacity = allowedCapacity;
                }
            }

            public void KillContainer()
            {
                _initialized = false;

                _itemContainer.Kill();
                _itemContainer = null;

                ItemManager.DoRemoves();
            }

            public void Open(BasePlayer looter)
            {
                if (_instance._openBackpacks.ContainsKey(looter))
                    return;

                _instance._openBackpacks.Add(looter, this);

                if (!_initialized)
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

                if (!_looters.Contains(looter))
                    _looters.Add(looter);

                if (!_instance.VerifyCanOpenBackpack(looter, OwnerId))
                    return;

                if (_hasPossibleOverflow && looter.userID == OwnerId)
                {
                    HandlePossiblyOverflowingItems(looter);
                }

                PlayerLootContainer(looter, _itemContainer);

                Interface.CallHook("OnBackpackOpened", looter, OwnerId, _itemContainer);
            }

            private void HandlePossiblyOverflowingItems(BasePlayer receiver)
            {
                // Close for other looters since we are going to alter the capacity
                foreach (var looter in _looters.ToArray())
                {
                    if (looter != receiver)
                    {
                        ForceCloseLooter(looter);
                    }
                }

                var allowedCapacity = GetAllowedCapacity();

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

                _hasPossibleOverflow = false;
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
                
                BaseEntity entity = GameManager.server.CreateEntity(BackpackPrefab, position, Quaternion.identity);
                DroppedItemContainer container = entity as DroppedItemContainer;

                // This needs to be set to "genericlarge" to allow up to 7 rows to be displayed.
                container.lootPanelName = "genericlarge";

                // The player name is being ignore due to the panelName being "genericlarge".
                // TODO: Try to figure out a way to have 7 rows with custom name.
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

                container.ResetRemovalTime();
                container.Spawn();

                ItemManager.DoRemoves();

                if (!_instance._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }

                return container;
            }

            public void EraseContents()
            {
                object hookResult = Interface.CallHook("CanEraseBackpack", OwnerId);

                if (hookResult is bool && (bool)hookResult == false)
                    return;

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

            public static Backpack Get(ulong id)
            {
                if (id == 0)
                    _instance.PrintWarning("Accessing backpack for ID 0! Please report this to the author with as many details as possible.");

                if (_instance._backpacks.ContainsKey(id))
                    return _instance._backpacks[id];
                
                var fileName = $"{_instance.Name}/{id}";

                TryMigrateData(fileName);

                Backpack backpack;
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(fileName))
                {
                    LoadData(out backpack, fileName);
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

                _instance._backpacks.Add(id, backpack);
                
                backpack.Initialize();

                return backpack;
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
                    foreach (var contentItem in Contents)
                        contentItem.ToItem().MoveToContainer(item.contents);
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
