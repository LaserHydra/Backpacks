// #define DEBUG_POOLING
// #define DEBUG_BACKPACK_LIFECYCLE

using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Converters;
using Oxide.Core.Configuration;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Backpacks", "LaserHydra", "3.8.0")]
    [Description("Allows players to have a Backpack which provides them extra inventory space.")]
    internal class Backpacks : CovalencePlugin
    {
        #region Fields

        private const int MinRows = 1;
        private const int MaxRows = 8;
        private const int MinCapacity = 1;
        private const int MaxCapacity = 48;
        private const int SlotsPerRow = 6;
        private const int ReclaimEntryMaxSize = 40;
        private const float StandardLootDelay = 0.1f;
        private const string GUIPanelName = "BackpacksUI";

        private const string UsagePermission = "backpacks.use";
        private const string SizePermission = "backpacks.size";
        private const string GUIPermission = "backpacks.gui";
        private const string FetchPermission = "backpacks.fetch";
        private const string AdminPermission = "backpacks.admin";
        private const string KeepOnDeathPermission = "backpacks.keepondeath";
        private const string KeepOnWipePermission = "backpacks.keeponwipe";
        private const string NoBlacklistPermission = "backpacks.noblacklist";

        private const string CoffinPrefab = "assets/prefabs/misc/halloween/coffin/coffinstorage.prefab";
        private const string DroppedBackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
        private const string ResizableLootPanelName = "generic_resizable";

        private const int SaddleBagItemId = 1400460850;

        private static readonly object True = true;
        private static readonly object False = false;

        private readonly BackpackCapacityManager _backpackCapacityManager;
        private readonly BackpackManager _backpackManager;
        private readonly ValueObjectCache<int> _intObjectCache = new ValueObjectCache<int>();
        private readonly ValueObjectCache<ulong> _ulongObjectCache = new ValueObjectCache<ulong>();

        private ProtectionProperties _immortalProtection;
        private string _cachedUI;

        private readonly ApiInstance _api;
        private Configuration _config;
        private StoredData _storedData;
        private int _wipeNumber;
        private readonly HashSet<ulong> _uiViewers = new HashSet<ulong>();
        private Coroutine _saveRoutine;

        [PluginReference]
        private readonly Plugin Arena, EventManager;

        public Backpacks()
        {
            _backpackCapacityManager = new BackpackCapacityManager(this);
            _backpackManager = new BackpackManager(this);
            _api = new ApiInstance(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init(this);

            permission.RegisterPermission(UsagePermission, this);
            permission.RegisterPermission(GUIPermission, this);
            permission.RegisterPermission(FetchPermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(KeepOnDeathPermission, this);
            permission.RegisterPermission(KeepOnWipePermission, this);
            permission.RegisterPermission(NoBlacklistPermission, this);

            _backpackCapacityManager.Init(_config);

            PoolUtils.ResizePools();

            _storedData = StoredData.Load();

            Unsubscribe(nameof(OnPlayerSleep));
            Unsubscribe(nameof(OnPlayerSleepEnded));
        }

        private void OnServerInitialized()
        {
            _wipeNumber = DetermineWipeNumber();

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

            RestartSaveRoutine(async: false, keepInUseBackpacks: false);

            BackpackNetworkController.ResetNetworkGroupId();

            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyGUI(player);
            }

            PoolUtils.ResizePools(empty: true);
        }

        private void OnNewSave(string filename)
        {
            if (!_config.ClearBackpacksOnWipe)
                return;

            _backpackManager.ClearCache();

            IEnumerable<string> fileNames;
            try
            {
                fileNames = Interface.Oxide.DataFileSystem.GetFiles(Name)
                    .Select(fn => {
                        return fn.Split(Path.DirectorySeparatorChar).Last()
                            .Replace(".json", string.Empty);
                    });
            }
            catch (DirectoryNotFoundException)
            {
                // No backpacks to clear.
                return;
            }

            var skippedBackpackCount = 0;

            foreach (var fileName in fileNames)
            {
                ulong userId;
                if (!ulong.TryParse(fileName, out userId))
                    continue;

                if (permission.UserHasPermission(fileName, KeepOnWipePermission))
                {
                    skippedBackpackCount++;
                    continue;
                }

                _backpackManager.ClearBackpackFile(userId);
            }

            var skippedBackpacksMessage = skippedBackpackCount > 0 ? $", except {skippedBackpackCount} due to being exempt" : string.Empty;
            LogWarning($"New save created. All backpacks were cleared{skippedBackpacksMessage}. Players with the '{KeepOnWipePermission}' permission are exempt. Clearing backpacks can be disabled for all players in the configuration file.");
        }

        private void OnServerSave()
        {
            RestartSaveRoutine(async: true, keepInUseBackpacks: true);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _backpackManager.GetCachedBackpack(player.userID)?.NetworkController?.Unsubscribe(player);
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
            {
                _backpackManager.TryEraseForPlayer(player.userID);
            }
            else if (_config.DropOnDeath)
            {
                _backpackManager.Drop(player.userID, player.transform.position);
            }
        }

        private void OnGroupPermissionGranted(string groupName, string perm)
        {
            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(NoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(GUIPermission))
            {
                foreach (var player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, groupName)))
                {
                    CreateGUI(player.Object as BasePlayer);
                }
            }
        }

        private void OnGroupPermissionRevoked(string groupName, string perm)
        {
            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(NoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(GUIPermission))
            {
                foreach (var player in covalence.Players.Connected.Where(p => permission.UserHasGroup(p.Id, groupName)))
                {
                    if (!player.HasPermission(GUIPermission))
                    {
                        DestroyGUI(player.Object as BasePlayer);
                    }
                }
            }
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForUser(userId);
            }
            else if (perm.Equals(NoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForUser(userId);
            }
            else if (perm.Equals(GUIPermission))
            {
                var player = BasePlayer.Find(userId);
                if (player != null)
                {
                    CreateGUI(BasePlayer.Find(userId));
                }
            }
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForUser(userId);
            }
            else if (perm.Equals(NoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForUser(userId);
            }
            else if (perm.Equals(GUIPermission) && !permission.UserHasPermission(userId, GUIPermission))
            {
                var player = BasePlayer.Find(userId);
                if (player != null)
                {
                    DestroyGUI(player);
                }
            }
        }

        private void OnPlayerConnected(BasePlayer player) => CreateGUI(player);

        private void OnPlayerRespawned(BasePlayer player) => CreateGUI(player);

        private void OnPlayerSleepEnded(BasePlayer player) => CreateGUI(player);

        private void OnPlayerSleep(BasePlayer player) => DestroyGUI(player);

        private void OnNetworkSubscriptionsUpdate(Network.Networkable networkable, List<Network.Visibility.Group> groupsToAdd, List<Network.Visibility.Group> groupsToRemove)
        {
            if (groupsToRemove == null)
                return;

            for (var i = groupsToRemove.Count - 1; i >= 0; i--)
            {
                var group = groupsToRemove[i];
                if (BackpackNetworkController.IsBackpackNetworkGroup(group))
                {
                    // Prevent automatically unsubscribing from backpack network groups.
                    // This allows the subscriptions to persist while players move around.
                    groupsToRemove.Remove(group);
                }
            }
        }

        #endregion

        #region API

        private class ApiInstance
        {
            public readonly Dictionary<string, object> ApiWrapper;

            private readonly Backpacks _plugin;
            private BackpackManager _backpackManager => _plugin._backpackManager;

            public ApiInstance(Backpacks plugin)
            {
                _plugin = plugin;

                ApiWrapper = new Dictionary<string, object>
                {
                    [nameof(GetExistingBackpacks)] = new Func<Dictionary<ulong, ItemContainer>>(GetExistingBackpacks),
                    [nameof(EraseBackpack)] = new Action<ulong>(EraseBackpack),
                    [nameof(DropBackpack)] = new Func<BasePlayer, DroppedItemContainer>(DropBackpack),
                    [nameof(GetBackpackOwnerId)] = new Func<ItemContainer, ulong>(GetBackpackOwnerId),
                    [nameof(GetBackpackContainer)] = new Func<ulong, ItemContainer>(GetBackpackContainer),
                    [nameof(GetBackpackItemAmount)] = new Func<ulong, int, ulong, int>(GetBackpackItemAmount),
                    [nameof(TryOpenBackpack)] = new Func<BasePlayer, ulong, bool>(TryOpenBackpack),
                    [nameof(TryOpenBackpackContainer)] = new Func<BasePlayer, ulong, ItemContainer, bool>(TryOpenBackpackContainer),
                    [nameof(TryOpenBackpackPage)] = new Func<BasePlayer, ulong, int, bool>(TryOpenBackpackPage),
                    [nameof(SumBackpackItems)] = new Func<ulong, Dictionary<string, object>, int>(SumBackpackItems),
                    [nameof(CountBackpackItems)] = new Func<ulong, Dictionary<string, object>, int>(CountBackpackItems),
                    [nameof(TakeBackpackItems)] = new Func<ulong, Dictionary<string, object>, int, List<Item>, int>(TakeBackpackItems),
                    [nameof(TryDepositBackpackItem)] = new Func<ulong, Item, bool>(TryDepositBackpackItem),
                    [nameof(WriteBackpackContentsFromJson)] = new Action<ulong, string>(WriteBackpackContentsFromJson),
                    [nameof(ReadBackpackContentsAsJson)] = new Func<ulong, string>(ReadBackpackContentsAsJson)
                };
            }

            public Dictionary<ulong, ItemContainer> GetExistingBackpacks()
            {
                return _backpackManager.GetAllCachedContainers();
            }

            public void EraseBackpack(ulong userId)
            {
                _backpackManager.TryEraseForPlayer(userId, force: true);
            }

            public DroppedItemContainer DropBackpack(BasePlayer player)
            {
                var backpack = _backpackManager.GetBackpackIfExists(player.userID);
                if (backpack == null)
                    return null;

                return _backpackManager.Drop(player.userID, player.transform.position);
            }

            public ulong GetBackpackOwnerId(ItemContainer container)
            {
                return _backpackManager.GetCachedBackpackForContainer(container)?.OwnerId ?? 0;
            }

            public ItemContainer GetBackpackContainer(ulong ownerId)
            {
                return _backpackManager.GetBackpackIfExists(ownerId)?.GetContainer(ensureContainer: true);
            }

            public int GetBackpackItemAmount(ulong ownerId, int itemId, ulong skinId)
            {
                var itemQuery = new ItemQuery { ItemId = itemId, SkinId = skinId };
                return _backpackManager.GetBackpackIfExists(ownerId)?.SumItems(ref itemQuery) ?? 0;
            }

            public bool TryOpenBackpack(BasePlayer player, ulong ownerId)
            {
                return _backpackManager.TryOpenBackpack(player, ownerId);
            }

            public bool TryOpenBackpackContainer(BasePlayer player, ulong ownerId, ItemContainer container)
            {
                return _backpackManager.TryOpenBackpackContainer(player, ownerId, container);
            }

            public bool TryOpenBackpackPage(BasePlayer player, ulong ownerId, int page)
            {
                return _backpackManager.TryOpenBackpackPage(player, ownerId, page);
            }

            public int SumBackpackItems(ulong ownerId, Dictionary<string, object> dict)
            {
                var itemQuery = ItemQuery.FromDict(dict);
                return _backpackManager.GetBackpackIfExists(ownerId)?.SumItems(ref itemQuery) ?? 0;
            }

            public int CountBackpackItems(ulong ownerId, Dictionary<string, object> dict)
            {
                var itemQuery = ItemQuery.FromDict(dict);
                return _backpackManager.GetBackpackIfExists(ownerId)?.CountItems(ref itemQuery) ?? 0;
            }

            public int TakeBackpackItems(ulong ownerId, Dictionary<string, object> dict, int amount, List<Item> collect)
            {
                var itemQuery = ItemQuery.FromDict(dict);
                return _backpackManager.GetBackpackIfExists(ownerId)?.TakeItems(ref itemQuery, amount, collect) ?? 0;
            }

            public bool TryDepositBackpackItem(ulong ownerId, Item item)
            {
                return _backpackManager.GetBackpack(ownerId).TryDepositItem(item);
            }

            public void WriteBackpackContentsFromJson(ulong ownerId, string json)
            {
                _backpackManager.GetBackpack(ownerId).WriteContentsFromJson(json);
            }

            public string ReadBackpackContentsAsJson(ulong ownerId)
            {
                return _backpackManager.GetBackpackIfExists(ownerId)?.SerializeContentsAsJson();
            }
        }

        [HookMethod(nameof(API_GetApi))]
        public Dictionary<string, object> API_GetApi()
        {
            return _api.ApiWrapper;
        }

        [HookMethod(nameof(API_GetExistingBackpacks))]
        public Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
        {
            return _api.GetExistingBackpacks();
        }

        [HookMethod(nameof(API_EraseBackpack))]
        public void API_EraseBackpack(ulong userId)
        {
            _api.EraseBackpack(userId);
        }

        [HookMethod(nameof(API_DropBackpack))]
        public DroppedItemContainer API_DropBackpack(BasePlayer player)
        {
            return _api.DropBackpack(player);
        }

        [HookMethod(nameof(API_GetBackpackOwnerId))]
        public object API_GetBackpackOwnerId(ItemContainer container)
        {
            return _ulongObjectCache.Get(_api.GetBackpackOwnerId(container));
        }

        [HookMethod(nameof(API_GetBackpackContainer))]
        public ItemContainer API_GetBackpackContainer(ulong ownerId)
        {
            return _api.GetBackpackContainer(ownerId);
        }

        [HookMethod(nameof(API_GetBackpackItemAmount))]
        public int API_GetBackpackItemAmount(ulong ownerId, int itemId, ulong skinId = 0)
        {
            return _api.GetBackpackItemAmount(ownerId, itemId, skinId);
        }

        [HookMethod(nameof(API_TryOpenBackpack))]
        public object API_TryOpenBackpack(BasePlayer player, ulong ownerId = 0, ItemContainer container = null)
        {
            return BooleanNoAlloc(_api.TryOpenBackpack(player, ownerId));
        }

        [HookMethod(nameof(API_TryOpenBackpackContainer))]
        public object API_TryOpenBackpackContainer(BasePlayer player, ulong ownerId, ItemContainer container)
        {
            return BooleanNoAlloc(_api.TryOpenBackpackContainer(player, ownerId, container));
        }

        [HookMethod(nameof(API_TryOpenBackpackPage))]
        public object API_TryOpenBackpackPage(BasePlayer player, ulong ownerId = 0, int page = 0)
        {
            return BooleanNoAlloc(_api.TryOpenBackpackPage(player, ownerId, page));
        }

        [HookMethod(nameof(API_SumBackpackItems))]
        public object API_SumBackpackItems(ulong ownerId, Dictionary<string, object> dict)
        {
            return _intObjectCache.Get(_api.SumBackpackItems(ownerId, dict));
        }

        [HookMethod(nameof(API_CountBackpackItems))]
        public object API_CountBackpackItems(ulong ownerId, Dictionary<string, object> dict)
        {
            return _intObjectCache.Get(_api.CountBackpackItems(ownerId, dict));
        }

        [HookMethod(nameof(API_TakeBackpackItems))]
        public object API_TakeBackpackItems(ulong ownerId, Dictionary<string, object> dict, int amount, List<Item> collect)
        {
            return _intObjectCache.Get(_api.TakeBackpackItems(ownerId, dict, amount, collect));
        }

        [HookMethod(nameof(API_TryDepositBackpackItem))]
        public object API_TryDepositBackpackItem(ulong ownerId, Item item)
        {
            return BooleanNoAlloc(_api.TryDepositBackpackItem(ownerId, item));
        }

        [HookMethod(nameof(API_WriteBackpackContentsFromJson))]
        public void API_WriteBackpackContentsFromJson(ulong ownerId, string json)
        {
            _api.WriteBackpackContentsFromJson(ownerId, json);
        }

        [HookMethod(nameof(API_ReadBackpackContentsAsJson))]
        public object API_ReadBackpackContentsAsJson(ulong ownerId)
        {
            return _api.ReadBackpackContentsAsJson(ownerId);
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object CanOpenBackpack(Backpacks plugin, BasePlayer looter, ulong ownerId)
            {
                return Interface.CallHook("CanOpenBackpack", looter, plugin._ulongObjectCache.Get(ownerId));
            }

            public static void OnBackpackClosed(Backpacks plugin, BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackClosed", looter, plugin._ulongObjectCache.Get(ownerId), container);
            }

            public static void OnBackpackOpened(Backpacks plugin, BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackOpened", looter, plugin._ulongObjectCache.Get(ownerId), container);
            }

            public static object CanDropBackpack(Backpacks plugin, ulong ownerId, Vector3 position)
            {
                return Interface.CallHook("CanDropBackpack", plugin._ulongObjectCache.Get(ownerId), position);
            }

            public static object CanEraseBackpack(Backpacks plugin, ulong ownerId)
            {
                return Interface.CallHook("CanEraseBackpack", plugin._ulongObjectCache.Get(ownerId));
            }

            public static object CanBackpackAcceptItem(Backpacks plugin, ulong ownerId, ItemContainer container, Item item)
            {
                return Interface.CallHook("CanBackpackAcceptItem", plugin._ulongObjectCache.Get(ownerId), container, item);
            }
        }

        #endregion

        #region Commands

        [Command("backpack", "backpack.open")]
        private void BackpackOpenCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                ParsePageArg(args.FirstOrDefault()),
                wrapAround: false
            );
        }

        [Command("backpack.next")]
        private void BackpackNextCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault())
            );
        }

        [Command("backpack.previous", "backpack.prev")]
        private void BackpackPreviousCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                forward: false
            );
        }

        [Command("backpack.fetch")]
        private void BackpackFetchCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, FetchPermission))
                return;

            if (args.Length < 2)
            {
                player.Reply(GetMessage(player, "Backpack Fetch Syntax"));
                return;
            }

            if (!VerifyCanOpenBackpack(basePlayer, basePlayer.userID))
                return;

            ItemDefinition itemDefinition;
            if (!VerifyValidItem(player, args[0], out itemDefinition))
                return;

            int desiredAmount;
            if (!int.TryParse(args[1], out desiredAmount) || desiredAmount < 1)
            {
                player.Reply(GetMessage(player, "Invalid Item Amount"));
                return;
            }

            var itemLocalizedName = itemDefinition.displayName.translated;
            var backpack = _backpackManager.GetBackpack(basePlayer.userID);

            var itemQuery = new ItemQuery { ItemDefinition = itemDefinition };

            var quantityInBackpack = backpack.SumItems(ref itemQuery);
            if (quantityInBackpack == 0)
            {
                player.Reply(string.Format(GetMessage(player, "Item Not In Backpack"), itemLocalizedName));
                return;
            }

            if (desiredAmount > quantityInBackpack)
            {
                desiredAmount = quantityInBackpack;
            }

            var amountTransferred = backpack.FetchItems(basePlayer, ref itemQuery, desiredAmount);
            if (amountTransferred <= 0)
            {
                player.Reply(string.Format(GetMessage(player, "Fetch Failed"), itemLocalizedName));
                return;
            }

            player.Reply(string.Format(GetMessage(player, "Items Fetched"), amountTransferred.ToString(), itemLocalizedName));
        }

        [Command("backpack.erase")]
        private void EraseBackpackCommand(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer)
                return;

            ulong userId;
            if (args.Length < 1 || !ulong.TryParse(args[0], out userId))
            {
                player.Reply($"Syntax: {cmd} <id>");
                return;
            }

            if (!_backpackManager.TryEraseForPlayer(userId, force: true))
            {
                LogWarning($"Player {userId.ToString()} has no backpack to erase.");
                return;
            }

            LogWarning($"Erased backpack for player {userId.ToString()}.");
        }

        [Command("viewbackpack")]
        private void ViewBackpackCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, AdminPermission))
                return;

            if (args.Length < 1)
            {
                player.Reply(GetMessage(player, "View Backpack Syntax"));
                return;
            }

            string failureMessage;
            var targetPlayer = FindPlayer(player, args[0], out failureMessage);

            if (targetPlayer == null)
            {
                player.Reply(failureMessage);
                return;
            }

            var targetBasePlayer = targetPlayer.Object as BasePlayer;
            var backpackOwnerId = targetBasePlayer?.userID ?? ulong.Parse(targetPlayer.Id);

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                ParsePageArg(args.ElementAtOrDefault(1)),
                ownerId: backpackOwnerId
            );
        }

        [Command("backpackgui")]
        private void ToggleBackpackGUI(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyPlayer(player, out basePlayer)
                || !VerifyHasPermission(player, GUIPermission))
                return;

            var enabledNow = _storedData.ToggleGuiButtonPreference(basePlayer.userID, _config.GUI.EnabledByDefault);
            if (enabledNow)
            {
                CreateGUI(basePlayer);
            }
            else
            {
                DestroyGUI(basePlayer);
            }

            player.Reply(GetMessage(player, "Toggled Backpack GUI"));
        }

        #endregion

        #region Helper Methods

        public static void LogDebug(string message) => Interface.Oxide.LogDebug($"[Backpacks] {message}");
        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Backpacks] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Backpacks] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Backpacks] {message}");

        private static object BooleanNoAlloc(bool value)
        {
            return value ? True : False;
        }

        private static bool IsKeyBindArg(string arg)
        {
            return arg == "True";
        }

        private static int ParsePageArg(string arg)
        {
            if (arg == null)
                return -1;

            int pageIndex;
            return int.TryParse(arg, out pageIndex)
                ? Math.Max(0, pageIndex - 1)
                : -1;
        }

        private static string DetermineLootPanelName(ItemContainer container)
        {
            return (container.entityOwner as StorageContainer)?.panelName
                   ?? (container.entityOwner as ContainerIOEntity)?.lootPanelName
                   ?? (container.entityOwner as LootableCorpse)?.lootPanelName
                   ?? (container.entityOwner as DroppedItemContainer)?.lootPanelName
                   ?? (container.entityOwner as BaseRidableAnimal)?.lootPanelName;
        }

        private static void ClosePlayerInventory(BasePlayer player)
        {
            player.ClientRPCPlayer(null, player, "OnRespawnInformation");
        }

        private static float CalculateOpenDelay(ItemContainer currentContainer, int nextContainerCapacity, bool isKeyBind = false)
        {
            if (currentContainer != null)
            {
                // Can instantly switch to a smaller container.
                if (nextContainerCapacity < currentContainer.capacity)
                    return 0;

                // Can instantly switch to a generic resizable loot panel from a different loot panel.
                if (DetermineLootPanelName(currentContainer) != ResizableLootPanelName)
                    return 0;

                // Need a short delay so the generic_resizable loot panel can be redrawn properly.
                return StandardLootDelay;
            }

            // Can open instantly since not looting and chat is assumed to be closed.
            if (isKeyBind)
                return 0;

            // Not opening via key bind, so the chat window may be open.
            // Must delay in case the chat is still closing or else the loot panel may close instantly.
            return StandardLootDelay;
        }

        private static int DetermineWipeNumber()
        {
            var saveName = World.SaveFileName;

            var lastDotIndex = saveName.LastIndexOf('.');
            var secondToLastDotIndex = saveName.LastIndexOf('.', lastDotIndex - 1);
            var wipeNumberString = saveName.Substring(secondToLastDotIndex + 1, lastDotIndex - secondToLastDotIndex - 1);

            int wipeNumber;
            return int.TryParse(wipeNumberString, out wipeNumber)
                ? wipeNumber
                : 0;
        }

        private static void StartLooting(BasePlayer player, ItemContainer container, StorageContainer entitySource)
        {
            if (player.CanInteract()
                && Interface.CallHook("CanLootEntity", player, entitySource) == null
                && player.inventory.loot.StartLootingEntity(entitySource, doPositionChecks: false))
            {
                player.inventory.loot.AddContainer(container);
                player.inventory.loot.SendImmediate();
                player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", entitySource.panelName);
            }
        }

        private static ItemContainer CreateItemContainer(int capacity, StorageContainer entityOwner)
        {
            var container = new ItemContainer();
            container.ServerInitialize(null, capacity);
            container.GiveUID();
            container.entityOwner = entityOwner;
            return container;
        }

        private IEnumerator SaveRoutine(bool async, bool keepInUseBackpacks)
        {
            if (_storedData.SaveIfChanged() && async)
                yield return null;

            yield return _backpackManager.SaveAllAndKill(async, keepInUseBackpacks);
        }

        private void RestartSaveRoutine(bool async, bool keepInUseBackpacks)
        {
            if (_saveRoutine != null)
            {
                ServerMgr.Instance.StopCoroutine(_saveRoutine);
            }

            ServerMgr.Instance.StartCoroutine(SaveRoutine(async, keepInUseBackpacks));
        }

        private void OpenBackpack(BasePlayer looter, bool isKeyBind, int desiredPageIndex = -1, bool forward = true, bool wrapAround = true, ulong ownerId = 0)
        {
            var playerLoot = looter.inventory.loot;
            var lootingContainer = playerLoot.containers.FirstOrDefault();

            var wasLooting = lootingContainer != null;
            if (wasLooting)
            {
                Backpack currentBackpack;
                int currentPageIndex;
                if (_backpackManager.IsBackpack(lootingContainer, out currentBackpack, out currentPageIndex)
                    && (currentBackpack.OwnerId == ownerId || ownerId == 0))
                {
                    var nextPageIndex = currentBackpack.DetermineNextPageIndexForLooter(looter.userID, currentPageIndex, desiredPageIndex, forward, wrapAround, requireContents: false);
                    if (nextPageIndex == currentPageIndex)
                    {
                        if (!wrapAround)
                        {
                            // Close the backpack.
                            looter.EndLooting();
                            ClosePlayerInventory(looter);
                        }
                        return;
                    }

                    var nextPageCapacity = currentBackpack.GetAllowedPageCapacityForLooter(looter.userID, nextPageIndex);
                    if (nextPageCapacity > lootingContainer.capacity)
                    {
                        playerLoot.Clear();
                        playerLoot.SendImmediate();

                        {
                            var backpack2 = currentBackpack;
                            var looter2 = looter;
                            var pageIndex2 = desiredPageIndex;
                            timer.Once(StandardLootDelay, () => backpack2.TryOpen(looter2, pageIndex2));
                        }
                        return;
                    }

                    currentBackpack.SwitchToPage(looter, nextPageIndex);
                    return;
                }
            }

            // At this point, player is not looting, looting a different backpack, or looting a different container.
            if (ownerId == 0)
            {
                ownerId = looter.userID;
            }

            var backpack = _backpackManager.GetBackpack(ownerId);
            int pageCapacity;
            desiredPageIndex = backpack.DetermineInitialPageForLooter(looter.userID, desiredPageIndex, forward, out pageCapacity);

            var delaySeconds = CalculateOpenDelay(lootingContainer, pageCapacity, isKeyBind);
            if (delaySeconds > 0)
            {
                if (wasLooting)
                {
                    looter.EndLooting();
                    playerLoot.SendImmediate();
                }

                var ownerId2 = ownerId;
                var looter2 = looter;
                var desiredPageIndex2 = desiredPageIndex;

                timer.Once(delaySeconds, () => _backpackManager.TryOpenBackpackPage(looter2, ownerId2, desiredPageIndex2));
                return;
            }

            _backpackManager.TryOpenBackpackPage(looter, ownerId, desiredPageIndex);
        }

        private bool ShouldDisplayGuiButton(BasePlayer player)
        {
            return _storedData.GetGuiButtonPreference(player.userID)
                ?? _config.GUI.EnabledByDefault;
        }

        private IPlayer FindPlayer(IPlayer requester, string nameOrID, out string failureMessage)
        {
            failureMessage = string.Empty;

            ulong userId;
            if (nameOrID.StartsWith("7656119") && nameOrID.Length == 17 && ulong.TryParse(nameOrID, out userId))
            {
                IPlayer player = covalence.Players.All.FirstOrDefault(p => p.Id == nameOrID);

                if (player == null)
                {
                    failureMessage = string.Format(GetMessage(requester, "User ID not Found"), nameOrID);
                }

                return player;
            }

            var foundPlayers = new List<IPlayer>();

            foreach (var player in covalence.Players.All)
            {
                if (player.Name.Equals(nameOrID, StringComparison.InvariantCultureIgnoreCase))
                    return player;

                if (player.Name.ToLower().Contains(nameOrID.ToLower()))
                {
                    foundPlayers.Add(player);
                }
            }

            switch (foundPlayers.Count)
            {
                case 0:
                    failureMessage = string.Format(GetMessage(requester, "User Name not Found"), nameOrID);
                    return null;

                case 1:
                    return foundPlayers[0];

                default:
                    string names = string.Join(", ", foundPlayers.Select(p => p.Name).ToArray());
                    failureMessage = string.Format(GetMessage(requester, "Multiple Players Found"), names);
                    return null;
            }
        }

        private bool VerifyPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            if (player.IsServer)
            {
                basePlayer = null;
                return false;
            }

            basePlayer = player.Object as BasePlayer;
            return true;
        }

        private bool VerifyHasPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
                return true;

            player.Reply(GetMessage(player, "No Permission"));
            return false;
        }

        private bool VerifyValidItem(IPlayer player, string itemArg, out ItemDefinition itemDefinition)
        {
            itemDefinition = ItemManager.FindItemDefinition(itemArg);
            if (itemDefinition != null)
                return true;

            // User may have provided an itemID instead of item short name
            int itemID;
            if (!int.TryParse(itemArg, out itemID))
            {
                player.Reply(GetMessage(player, "Invalid Item"));
                return false;
            }

            itemDefinition = ItemManager.FindItemDefinition(itemID);
            if (itemDefinition != null)
                return true;

            player.Reply(GetMessage(player, "Invalid Item"));
            return false;
        }

        private bool VerifyCanInteract(IPlayer player, out BasePlayer basePlayer)
        {
            return VerifyPlayer(player, out basePlayer)
                   && basePlayer.CanInteract();
        }

        private bool VerifyCanOpenBackpack(BasePlayer looter, ulong ownerId)
        {
            if (IsPlayingEvent(looter))
            {
                looter.ChatMessage(GetMessage(looter, "May Not Open Backpack In Event"));
                return false;
            }

            var hookResult = ExposedHooks.CanOpenBackpack(this, looter, ownerId);
            if (hookResult != null && hookResult is string)
            {
                looter.ChatMessage(hookResult as string);
                return false;
            }

            return true;
        }

        private bool IsPlayingEvent(BasePlayer player)
        {
            // Multiple event/arena plugins define the isEventPlayer method as a standard.
            var isPlaying = Interface.CallHook("isEventPlayer", player);
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

            if (!ShouldDisplayGuiButton(player))
                return;

            DestroyGUI(player);
            _uiViewers.Add(player.userID);

            if (_cachedUI == null)
            {
                var cuiElements = new CuiElementContainer();

                cuiElements.Add(new CuiElement
                {
                    Name = GUIPanelName,
                    Parent = "Hud.Menu",
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Color = _config.GUI.Color,
                            Sprite = "assets/content/ui/ui.background.tiletex.psd",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = _config.GUI.GUIButtonPosition.AnchorsMin,
                            AnchorMax = _config.GUI.GUIButtonPosition.AnchorsMax,
                            OffsetMin = _config.GUI.GUIButtonPosition.OffsetsMin,
                            OffsetMax = _config.GUI.GUIButtonPosition.OffsetsMax
                        },
                    },
                });

                var imageComponent = _config.GUI.SkinId != 0
                    ? new CuiImageComponent { ItemId = SaddleBagItemId, SkinId = _config.GUI.SkinId }
                    : new CuiRawImageComponent { Url = _config.GUI.Image } as ICuiComponent;

                cuiElements.Add(new CuiElement
                {
                    Parent = GUIPanelName,
                    Components = {
                        imageComponent,
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                        },
                    }
                });

                cuiElements.Add(new CuiButton
                {
                    Button =
                    {
                        Command = "backpack.open",
                        Color = "0 0 0 0",
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                    },
                }, GUIPanelName);

                _cachedUI = CuiHelper.ToJson(cuiElements);
            }

            CuiHelper.AddUi(player, _cachedUI);
        }

        private void DestroyGUI(BasePlayer player)
        {
            if (!_uiViewers.Remove(player.userID))
                return;

            CuiHelper.DestroyUi(player, GUIPanelName);
        }

        #endregion

        #region Helper Classes

        private static class StringUtils
        {
            public static bool Equals(string a, string b) =>
                string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;

            public static bool Contains(string haystack, string needle) =>
                haystack.Contains(needle, CompareOptions.IgnoreCase);
        }

        private class ValueObjectCache<T> where T : struct
        {
            private readonly Dictionary<T, object> _cache = new Dictionary<T, object>();

            public object Get(T val)
            {
                object cachedObject;
                if (!_cache.TryGetValue(val, out cachedObject))
                {
                    cachedObject = val;
                    _cache[val] = cachedObject;
                }
                return cachedObject;
            }
        }

        private class PoolConverter<T> : CustomCreationConverter<T> where T : class, new()
        {
            public override T Create(Type objectType)
            {
                #if DEBUG_POOLING
                LogDebug($"{typeof(PoolConverter<T>).Name}<{objectType.Name}>::Create");
                #endif

                return Pool.Get<T>();
            }
        }

        private class PoolListConverter<T> : CustomCreationConverter<List<T>> where T : class, new()
        {
            public override List<T> Create(Type objectType)
            {
                #if DEBUG_POOLING
                LogDebug($"{typeof(PoolListConverter<T>).Name}<{objectType.Name}>::Create");
                #endif

                return Pool.GetList<T>();
            }
        }

        private static class ItemUtils
        {
            public static bool HasItemMod<T>(ItemDefinition itemDefinition) where T : ItemMod
            {
                foreach (var itemMod in itemDefinition.itemMods)
                {
                    if (itemMod is T)
                        return true;
                }

                return false;
            }

            public static bool HasSearchableContainer(Item item, out List<Item> itemList)
            {
                itemList = item.contents?.itemList;
                return itemList?.Count > 0 && HasSearchableContainer(item.info);
            }

            public static bool HasSearchableContainer(ItemData itemData, out List<ItemData> itemDataList)
            {
                itemDataList = itemData.Contents;
                return itemDataList?.Count > 0 && HasSearchableContainer(itemData.ID);
            }

            public static int CountItems(List<Item> itemList, ref ItemQuery itemQuery)
            {
                var count = 0;

                foreach (var item in itemList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        count++;
                    }

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        count += CountItems(childItems, ref itemQuery);
                    }
                }

                return count;
            }

            public static int CountItems(List<ItemData> itemDataList, ref ItemQuery itemQuery)
            {
                var count = 0;

                foreach (var itemData in itemDataList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(itemData);
                    if (usableAmount > 0)
                    {
                        count++;
                    }

                    List<ItemData> childItems;
                    if (HasSearchableContainer(itemData, out childItems))
                    {
                        count += CountItems(childItems, ref itemQuery);
                    }
                }

                return count;
            }

            public static int SumItems(List<Item> itemList, ref ItemQuery itemQuery)
            {
                var sum = 0;

                foreach (var item in itemList)
                {
                    sum += itemQuery.GetUsableAmount(item);

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        sum += SumItems(childItems, ref itemQuery);
                    }
                }

                return sum;
            }

            public static int SumItems(List<ItemData> itemDataList, ref ItemQuery itemQuery)
            {
                var sum = 0;

                foreach (var itemData in itemDataList)
                {
                    sum += itemQuery.GetUsableAmount(itemData);

                    List<ItemData> childItemList;
                    if (HasSearchableContainer(itemData, out childItemList))
                    {
                        sum += SumItems(childItemList, ref itemQuery);
                    }
                }

                return sum;
            }

            public static int TakeItems(List<Item> itemList, List<Item> collect, ref ItemQuery itemQuery, int amount)
            {
                var totalAmountTaken = 0;

                for (var i = itemList.Count - 1; i >= 0; i--)
                {
                    var item = itemList[i];
                    var amountToTake = amount - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        amountToTake = Math.Min(usableAmount, amountToTake);

                        TakeItemAmount(item, collect, amountToTake);
                        totalAmountTaken += amountToTake;
                    }

                    amountToTake = amount - totalAmountTaken;
                    List<Item> childItemList;
                    if (amountToTake > 0 && HasSearchableContainer(item, out childItemList))
                    {
                        totalAmountTaken += TakeItems(childItemList, collect, ref itemQuery, amount);
                    }
                }

                return totalAmountTaken;
            }

            public static int TakeItems(List<ItemData> itemDataList, List<Item> collect, ref ItemQuery itemQuery, int amount)
            {
                var totalAmountTaken = 0;

                for (var i = itemDataList.Count - 1; i >= 0; i--)
                {
                    var itemData = itemDataList[i];
                    var amountToTake = amount - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var usableAmount = itemQuery.GetUsableAmount(itemData);
                    if (usableAmount > 0)
                    {
                        amountToTake = Math.Min(usableAmount, amountToTake);

                        collect?.Add(itemData.ToItem(amountToTake));
                        itemData.Reduce(amountToTake);

                        if (itemData.Amount <= 0)
                        {
                            itemDataList.RemoveAt(i);
                            Pool.Free(ref itemData);
                        }

                        totalAmountTaken += amountToTake;
                    }

                    amountToTake = amount - totalAmountTaken;
                    List<ItemData> childItemList;
                    if (amountToTake > 0 && HasSearchableContainer(itemData, out childItemList))
                    {
                        totalAmountTaken += TakeItems(childItemList, collect, ref itemQuery, amountToTake);
                    }
                }

                return totalAmountTaken;
            }

            public static void SerializeForNetwork(List<Item> itemList, List<ProtoBuf.Item> collect)
            {
                foreach (var item in itemList)
                {
                    collect.Add(item.Save());

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        SerializeForNetwork(childItems, collect);
                    }
                }
            }

            public static void SerializeForNetwork(List<ItemData> itemDataList, List<ProtoBuf.Item> collect)
            {
                foreach (var itemData in itemDataList)
                {
                    var serializedItemData = Pool.Get<ProtoBuf.Item>();
                    serializedItemData.itemid = itemData.ID;
                    serializedItemData.amount = itemData.Amount;

                    if (itemData.DataInt != 0 || itemData.BlueprintTarget != 0)
                    {
                        if (serializedItemData.instanceData == null)
                        {
                            serializedItemData.instanceData = Pool.Get<ProtoBuf.Item.InstanceData>();
                        }

                        serializedItemData.instanceData.dataInt = itemData.DataInt;
                        serializedItemData.instanceData.blueprintTarget = itemData.BlueprintTarget;
                    }

                    collect.Add(serializedItemData);

                    List<ItemData> childItemList;
                    if (HasSearchableContainer(itemData, out childItemList))
                    {
                        SerializeForNetwork(childItemList, collect);
                    }
                }
            }

            private static bool HasSearchableContainer(ItemDefinition itemDefinition)
            {
                // Don't consider vanilla containers searchable (i.e., don't take low grade out of a miner's hat).
                return !HasItemMod<ItemModContainer>(itemDefinition);
            }

            private static bool HasSearchableContainer(int itemId)
            {
                var itemDefinition = ItemManager.FindItemDefinition(itemId);
                if ((object)itemDefinition == null)
                    return false;

                return HasSearchableContainer(itemDefinition);
            }

            private static void TakeItemAmount(Item item, List<Item> collect, int amount)
            {
                if (amount >= item.amount)
                {
                    item.RemoveFromContainer();
                    if (collect != null)
                    {
                        collect.Add(item);
                    }
                    else
                    {
                        item.Remove();
                    }
                }
                else
                {
                    if (collect != null)
                    {
                        collect.Add(item.SplitItem(amount));
                    }
                    else
                    {
                        item.amount -= amount;
                        item.MarkDirty();
                    }
                }
            }
        }

        #endregion

        #region Pooling

        private static class PoolUtils
        {
            public const int BackpackPoolSize = 500;

            public static void ResetItemsAndClear<T>(IList<T> list) where T : class, Pool.IPooled
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item == null)
                        continue;

                    Pool.Free(ref item);
                }

                if (list.IsReadOnly)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        list[i] = null;
                    }
                }
                else
                {
                    list.Clear();
                }
            }

            public static void ResizePools(bool empty = false)
            {
                ResetPool<ItemData>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<List<ItemData>>(empty ? 0 : BackpackPoolSize);
                ResetPool<Backpack>(empty ? 0 : BackpackPoolSize);
                ResetPool<VirtualContainerAdapter>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<ItemContainerAdapter>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<DisposableList<Item>>(empty ? 0 : 4);
                ResetPool<DisposableList<ItemData>>(empty ? 0 : 4);
            }

            #if DEBUG_POOLING
            public static string GetStats<T>() where T : class
            {
                var pool = Pool.FindCollection<T>();
                return $"{typeof(T).Name} | {pool.ItemsInUse.ToString()} used of {pool.ItemsCreated.ToString()} created | {pool.ItemsTaken.ToString()} taken";
            }
            #endif

            private static void ResetPool<T>(int size = 512) where T : class
            {
                var pool = Pool.FindCollection<T>();
                pool.Reset();
                pool.buffer = new T[size];
                Pool.directory.Remove(typeof(T));
            }
        }

        private class DisposableList<T> : List<T>, IDisposable
        {
            public static DisposableList<T> Get()
            {
                return Pool.Get<DisposableList<T>>();
            }

            public void Dispose()
            {
                Clear();
                var self = this;
                Pool.Free(ref self);
            }
        }

        #endregion

        #region Backpack Capacity Manager

        private class BackpackCapacityManager
        {
            private class BackpackSize
            {
                public readonly int Capacity;
                public readonly string Permission;

                public BackpackSize(int capacity, string permission)
                {
                    Capacity = capacity;
                    Permission = permission;
                }
            }

            private readonly Backpacks _plugin;
            private Configuration _config;
            private BackpackSize[] _sortedBackpackSizes;

            public BackpackCapacityManager(Backpacks plugin)
            {
                _plugin = plugin;
            }

            public void Init(Configuration config)
            {
                _config = config;

                var backpackSizeList = new List<BackpackSize>();

                if (config.EnableLegacyRowPermissions)
                {
                    for (var row = MinRows; row <= MaxRows; row++)
                    {
                        var backpackSize = new BackpackSize(row * SlotsPerRow, $"{UsagePermission}.{row.ToString()}");
                        _plugin.permission.RegisterPermission(backpackSize.Permission, _plugin);
                        backpackSizeList.Add(backpackSize);
                    }
                }

                foreach (var capacity in new HashSet<int>(config.BackpackPermissionSizes))
                {
                    backpackSizeList.Add(new BackpackSize(capacity, $"{SizePermission}.{capacity.ToString()}"));
                }

                backpackSizeList.Sort((a, b) => a.Capacity.CompareTo(b.Capacity));
                _sortedBackpackSizes = backpackSizeList.ToArray();

                foreach (var backpackSize in _sortedBackpackSizes)
                {
                    // The "backpacks.use.X" perms are registered all at once to make them easier to view.
                    if (backpackSize.Permission.StartsWith(UsagePermission))
                        continue;

                    _plugin.permission.RegisterPermission(backpackSize.Permission, _plugin);
                }
            }

            public int DetermineCapacity(string userIdString)
            {
                if (!_plugin.permission.UserHasPermission(userIdString, UsagePermission))
                    return 0;

                for (var i = _sortedBackpackSizes.Length - 1; i >= 0; i--)
                {
                    var backpackSize = _sortedBackpackSizes[i];
                    if (_plugin.permission.UserHasPermission(userIdString, backpackSize.Permission))
                    {
                        return backpackSize.Capacity;
                    }
                }

                return _config.DefaultBackpackSize;
            }
        }

        #endregion

        #region Backpack Manager

        private class BackpackManager
        {
            private static string DetermineBackpackPath(ulong userId) => $"{nameof(Backpacks)}/{userId.ToString()}";

            private readonly Backpacks _plugin;

            private readonly Dictionary<ulong, Backpack> _cachedBackpacks = new Dictionary<ulong, Backpack>();
            private readonly Dictionary<ulong, string> _backpackPathCache = new Dictionary<ulong, string>();
            private readonly Dictionary<ItemContainer, Backpack> _backpackContainers = new Dictionary<ItemContainer, Backpack>();

            private readonly List<Backpack> _tempBackpackList = new List<Backpack>(PoolUtils.BackpackPoolSize);

            public BackpackManager(Backpacks plugin)
            {
                _plugin = plugin;
            }

            public void HandleCapacityPermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    backpack.AllowedCapacityNeedsRefresh = true;
                }
            }

            public void HandleCapacityPermissionChangedForUser(string userIdString)
            {
                ulong userId;
                if (!ulong.TryParse(userIdString, out userId))
                    return;

                Backpack backpack;
                if (!_cachedBackpacks.TryGetValue(userId, out backpack))
                    return;

                backpack.AllowedCapacityNeedsRefresh = true;
            }

            public void HandleRestrictionPermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    backpack.RespectsItemRestrictionsNeedsRefresh = true;
                }
            }

            public void HandleRestrictionPermissionChangedForUser(string userIdString)
            {
                ulong userId;
                if (!ulong.TryParse(userIdString, out userId))
                    return;

                Backpack backpack;
                if (!_cachedBackpacks.TryGetValue(userId, out backpack))
                    return;

                backpack.RespectsItemRestrictionsNeedsRefresh = true;
            }

            public bool IsBackpack(ItemContainer container, out Backpack backpack, out int pageIndex)
            {
                if (!_backpackContainers.TryGetValue(container, out backpack))
                {
                    pageIndex = 0;
                    return false;
                }

                pageIndex = backpack.GetPageIndexForContainer(container);
                if (pageIndex == -1)
                {
                    pageIndex = 0;
                    return false;
                }

                return true;
            }

            public bool HasBackpackFile(ulong userId)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(GetBackpackPath(userId));
            }

            public Backpack GetCachedBackpack(ulong userId)
            {
                Backpack backpack;
                return _cachedBackpacks.TryGetValue(userId, out backpack)
                    ? backpack
                    : null;
            }

            public Backpack GetBackpack(ulong userId)
            {
                return GetCachedBackpack(userId) ?? Load(userId);
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

                foreach (var entry in _cachedBackpacks)
                {
                    var container = entry.Value.GetContainer();
                    if (container != null)
                        cachedContainersByUserId[entry.Key] = container;
                }

                return cachedContainersByUserId;
            }

            public DroppedItemContainer Drop(ulong userId, Vector3 position)
            {
                return GetBackpackIfExists(userId)?.Drop(position);
            }

            public bool TryOpenBackpack(BasePlayer looter, ulong backpackOwnerId)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                return GetBackpack(backpackOwnerId).TryOpen(looter);
            }

            public bool TryOpenBackpackContainer(BasePlayer looter, ulong backpackOwnerId, ItemContainer container)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                Backpack backpack;
                int pageIndex;
                if (!IsBackpack(container, out backpack, out pageIndex) || backpack.OwnerId != backpackOwnerId)
                {
                    backpack = GetBackpack(backpackOwnerId);
                    pageIndex = -1;
                }

                return backpack.TryOpen(looter, pageIndex);
            }

            public bool TryOpenBackpackPage(BasePlayer looter, ulong backpackOwnerId, int pageIndex = -1)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                return GetBackpack(backpackOwnerId).TryOpen(looter, pageIndex);
            }

            private string GetBackpackPath(ulong userId)
            {
                string filepath;
                if (!_backpackPathCache.TryGetValue(userId, out filepath))
                {
                    filepath = DetermineBackpackPath(userId);
                    _backpackPathCache[userId] = filepath;
                }

                return filepath;
            }

            private Backpack Load(ulong userId)
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Load | {userId.ToString()}");
                #endif

                var filePath = GetBackpackPath(userId);

                Backpack backpack = null;

                var dataFile = Interface.Oxide.DataFileSystem.GetFile(filePath);
                if (dataFile.Exists())
                {
                    backpack = dataFile.ReadObject<Backpack>();
                }

                // Note: Even if the user has a backpack file, the file contents may be null in some edge cases.
                // For example, if a data file cleaner plugin writes the file content as `null`.
                if (backpack == null)
                {
                    backpack = Pool.Get<Backpack>();
                }

                backpack.Setup(_plugin, userId, dataFile);
                _cachedBackpacks[userId] = backpack;

                return backpack;
            }

            public void ClearBackpackFile(ulong userId)
            {
                Interface.Oxide.DataFileSystem.WriteObject<object>(DetermineBackpackPath(userId), null);
            }

            public bool TryEraseForPlayer(ulong userId, bool force = false)
            {
                var backpack = GetBackpackIfExists(userId);
                if (backpack == null)
                    return false;

                backpack.EraseContents(force);
                return true;
            }

            public IEnumerator SaveAllAndKill(bool async, bool keepInUseBackpacks)
            {
                // Clear the list before usage, in case an error prevented cleanup, or in case coroutine was restarted.
                _tempBackpackList.Clear();

                // Copy the list of cached backpacks because it may be modified.
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    _tempBackpackList.Add(backpack);
                }

                foreach (var backpack in _tempBackpackList)
                {
                    var didSave = backpack.SaveIfChanged();

                    // Kill the backpack to free up space, if no admins are viewing it and its owner is disconnected.
                    if (!keepInUseBackpacks || (!backpack.HasLooters && BasePlayer.FindByID(backpack.OwnerId) == null))
                    {
                        backpack.Kill();
                        _cachedBackpacks.Remove(backpack.OwnerId);
                        _backpackPathCache.Remove(backpack.OwnerId);
                        var backpackToFree = backpack;
                        Pool.Free(ref backpackToFree);
                    }

                    if (didSave && async)
                        yield return null;
                }

                _tempBackpackList.Clear();
            }

            public void ClearCache()
            {
                _cachedBackpacks.Clear();
            }
        }

        #endregion

        #region Backpack Networking

        private class BackpackNetworkController
        {
            private const uint StartNetworkGroupId = 10000000;
            private static uint _nextNetworkGroupId = StartNetworkGroupId;

            public static void ResetNetworkGroupId()
            {
                _nextNetworkGroupId = StartNetworkGroupId;
            }

            public static bool IsBackpackNetworkGroup(Network.Visibility.Group group)
            {
                return group.ID >= StartNetworkGroupId && group.ID < _nextNetworkGroupId;
            }

            public static BackpackNetworkController Create()
            {
                return new BackpackNetworkController(_nextNetworkGroupId++);
            }

            public readonly Network.Visibility.Group NetworkGroup;

            private readonly List<BasePlayer> _subscribers = new List<BasePlayer>(1);

            private BackpackNetworkController(uint networkGroupId)
            {
                NetworkGroup = new Network.Visibility.Group(null, networkGroupId);
            }

            public void Subscribe(BasePlayer player)
            {
                if (player.Connection == null || _subscribers.Contains(player))
                    return;

                _subscribers.Add(player);

                // Send the client a message letting them know they are subscribed to the group.
                ServerMgr.OnEnterVisibility(player.Connection, NetworkGroup);

                // Send the client a snapshot of every entity currently in the group.
                // Don't use the entity queue for this because it could be cleared which could cause updates to be missed.
                foreach (var networkable in NetworkGroup.networkables)
                {
                    (networkable.handler as BaseNetworkable).SendAsSnapshot(player.Connection);
                }

                if (!NetworkGroup.subscribers.Contains(player.Connection))
                {
                    // Register the client with the group so that entities added to it will be automatically sent to the client.
                    NetworkGroup.subscribers.Add(player.Connection);
                }

                var subscriber = player.net.subscriber;
                if (!subscriber.subscribed.Contains(NetworkGroup))
                {
                    // Register the group with the client so that ShouldNetworkTo() returns true in SendNetworkUpdate().
                    // This covers cases such as toggling a pager's silent mode.
                    subscriber.subscribed.Add(NetworkGroup);
                }
            }

            public void Unsubscribe(BasePlayer player)
            {
                if (!_subscribers.Remove(player))
                    return;

                if (player.Connection == null)
                    return;

                // Unregister the client from the group so they don't get future entity updates.
                NetworkGroup.subscribers.Remove(player.Connection);
                player.net.subscriber.subscribed.Remove(NetworkGroup);

                // Send the client a message so they kill all client-side entities in the group.
                ServerMgr.OnLeaveVisibility(player.Connection, NetworkGroup);
            }

            public void UnsubscribeAll()
            {
                for (var i = _subscribers.Count - 1; i >= 0; i--)
                {
                    Unsubscribe(_subscribers[i]);
                }
            }
        }

        #endregion

        #region Unity Components

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

        private class BackpackCloseListener : EntityComponent<StorageContainer>
        {
            public static void AddToBackpackStorage(Backpacks plugin, StorageContainer containerEntity, Backpack backpack)
            {
                var component = containerEntity.gameObject.AddComponent<BackpackCloseListener>();
                component._plugin = plugin;
                component._backpack = backpack;
            }

            private Backpacks _plugin;
            private Backpack _backpack;

            // Called via `entity.SendMessage("PlayerStoppedLooting", player)` in PlayerLoot.Clear().
            private void PlayerStoppedLooting(BasePlayer looter)
            {
                _plugin.TrackStart();
                _backpack.OnClosed(looter);
                ExposedHooks.OnBackpackClosed(_plugin, looter, _backpack.OwnerId, looter.inventory.loot.containers.FirstOrDefault());
                _plugin.TrackEnd();
            }
        }

        #endregion

        #region Item Query

        private struct ItemQuery
        {
            public static ItemQuery FromDict(Dictionary<string, object> dict)
            {
                return new ItemQuery
                {
                    BlueprintId = GetOption<int>(dict, "BlueprintId"),
                    DisplayName = GetOption<string>(dict, "DisplayName"),
                    DataInt = GetOption<int>(dict, "DataInt"),
                    Flags = GetOption<int>(dict, "Flags"),
                    ItemDefinition = GetOption<ItemDefinition>(dict, "ItemDefinition"),
                    ItemId = GetOption<int>(dict, "ItemId"),
                    MinCondition = GetOption<float>(dict, "MinCondition"),
                    RequireEmpty = GetOption<bool>(dict, "RequireEmpty"),
                    SkinId = GetOption<ulong>(dict, "SkinId"),
                };
            }

            private static T GetOption<T>(Dictionary<string, object> dict, string key)
            {
                object value;
                return dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }

            public int BlueprintId;
            public int DataInt;
            public string DisplayName;
            public int Flags;
            public ItemDefinition ItemDefinition;
            public int ItemId;
            public float MinCondition;
            public bool RequireEmpty;
            public ulong SkinId;

            private int GetItemId()
            {
                if (ItemDefinition != null)
                    return ItemDefinition?.itemid ?? ItemId;

                return ItemId;
            }

            private ItemDefinition GetItemDefinition()
            {
                if ((object)ItemDefinition == null)
                {
                    ItemDefinition = ItemManager.FindItemDefinition(ItemId);
                }

                return ItemDefinition;
            }

            private bool HasCondition()
            {
                return GetItemDefinition()?.condition.enabled ?? false;
            }

            private float ConditionNormalized(ItemData itemData)
            {
                return itemData.Condition / itemData.MaxCondition;
            }

            private float MaxConditionNormalized(ItemData itemData)
            {
                var itemDefinition = GetItemDefinition();
                if (itemDefinition == null)
                    return 1;

                return itemData.MaxCondition / itemDefinition.condition.max;
            }

            public int GetUsableAmount(Item item)
            {
                var itemId = GetItemId();
                if (itemId != 0 && itemId != item.info.itemid)
                    return 0;

                if (SkinId != 0 && SkinId != item.skin)
                    return 0;

                if (BlueprintId != 0 && BlueprintId != item.blueprintTarget)
                    return 0;

                if (DataInt != 0 && DataInt != (item.instanceData?.dataInt ?? 0))
                    return 0;

                if (Flags != 0 && Flags != ((int)item.flags & Flags))
                    return 0;

                if (MinCondition > 0 && HasCondition() && (item.conditionNormalized < MinCondition || item.maxConditionNormalized < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, item.name))
                    return 0;

                return RequireEmpty && item.contents?.itemList?.Count > 0
                    ? Math.Max(0, item.amount - 1)
                    : item.amount;
            }

            public int GetUsableAmount(ItemData itemData)
            {
                var itemId = GetItemId();
                if (itemId != 0 && itemId != itemData.ID)
                    return 0;

                if (SkinId != 0 && SkinId != itemData.Skin)
                    return 0;

                if (BlueprintId != 0 && BlueprintId != itemData.BlueprintTarget)
                    return 0;

                if (DataInt != 0 && DataInt != itemData.DataInt)
                    return 0;

                if (Flags != 0 && Flags != ((int)itemData.Flags & Flags))
                    return 0;

                if (MinCondition > 0 && HasCondition() && (ConditionNormalized(itemData) < MinCondition || MaxConditionNormalized(itemData) < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, itemData.Name))
                    return 0;

                return RequireEmpty && itemData.Contents?.Count > 0
                    ? Math.Max(0, itemData.Amount - 1)
                    : itemData.Amount;
            }
        }

        #endregion

        #region Container Adapters

        private interface IContainerAdapter : Pool.IPooled
        {
            int PageIndex { get; }
            int Capacity { get; set; }
            bool HasItems { get; }
            int CountItems(ref ItemQuery itemQuery);
            int SumItems(ref ItemQuery itemQuery);
            int TakeItems(List<Item> collect, ref ItemQuery itemQuery, int amount);
            bool TryDepositItem(Item item);
            void ReclaimFractionForSoftcore(List<Item> collect, float fraction);
            void TakeRestrictedItems(List<Item> collect);
            void TakeAllItems(List<Item> collect, int startPosition = 0);
            void SerializeForNetwork(List<ProtoBuf.Item> saveList);
            void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool);
            void EraseContents();
            void Kill();
        }

        private class VirtualContainerAdapter : IContainerAdapter
        {
            public int PageIndex { get; private set; }
            public int Capacity { get; set; }
            public List<ItemData> ItemDataList { get; } = new List<ItemData>(MaxCapacity);
            public bool HasItems => ItemDataList.Count > 0;

            private Backpack _backpack;

            private Backpacks _plugin => _backpack.Plugin;
            private Configuration _config => _plugin._config;

            public VirtualContainerAdapter Setup(Backpack backpack, int pageIndex, int capacity)
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::Setup | PageIndex: {pageIndex.ToString()} | Capacity: {capacity.ToString()}");
                #endif

                PageIndex = pageIndex;
                Capacity = capacity;
                _backpack = backpack;
                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::EnterPool | {PoolUtils.GetStats<VirtualContainerAdapter>()}");
                #endif

                PageIndex = 0;
                Capacity = 0;
                PoolUtils.ResetItemsAndClear(ItemDataList);
                _backpack = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::LeavePool | {PoolUtils.GetStats<VirtualContainerAdapter>()}");
                #endif
            }

            public void SortByPosition()
            {
                ItemDataList.Sort((a, b) => a.Position.CompareTo(b.Position));
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.CountItems(ItemDataList, ref itemQuery);
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.SumItems(ItemDataList, ref itemQuery);
            }

            public int TakeItems(List<Item> collect, ref ItemQuery itemQuery, int amount)
            {
                var amountTaken = ItemUtils.TakeItems(ItemDataList, collect, ref itemQuery, amount);
                if (amountTaken > 0)
                {
                    _backpack.IsDirty = true;
                }

                return amountTaken;
            }

            public void ReclaimFractionForSoftcore(List<Item> collect, float fraction)
            {
                // For some reason, the vanilla reclaim logic doesn't take the last item.
                if (ItemDataList.Count <= 1)
                    return;

                var numToTake = Mathf.Ceil(ItemDataList.Count * fraction);

                for (var i = 0; i < numToTake; i++)
                {
                    var indexToTake = UnityEngine.Random.Range(0, ItemDataList.Count);
                    var itemDataToTake = ItemDataList[indexToTake];
                    if (itemDataToTake.Amount > 1)
                    {
                        // Prefer taking a smaller stack if possible (vanilla behavior).
                        for (var j = 0; j < ItemDataList.Count; j++)
                        {
                            var alternateItemData = ItemDataList[j];
                            if (alternateItemData.ID != itemDataToTake.ID)
                                continue;

                            if (alternateItemData.Amount >= itemDataToTake.Amount)
                                continue;

                            itemDataToTake = alternateItemData;
                            indexToTake = j;
                        }
                    }

                    var item = itemDataToTake.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    ItemDataList.RemoveAt(indexToTake);
                    Pool.Free(ref itemDataToTake);
                    _backpack.IsDirty = true;
                }
            }

            public void TakeRestrictedItems(List<Item> collect)
            {
                if (ItemDataList.Count == 0)
                    return;

                for (var i = ItemDataList.Count - 1; i >= 0; i--)
                {
                    var itemData = ItemDataList[i];
                    if (!_config.IsRestrictedItem(itemData))
                        continue;

                    var item = itemData.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    ItemDataList.RemoveAt(i);
                    Pool.Free(ref itemData);
                    _backpack.IsDirty = true;
                }
            }

            public void TakeAllItems(List<Item> collect, int startPosition = 0)
            {
                SortByPosition();

                if (ItemDataList.Count == 0)
                    return;

                for (var i = 0; i < ItemDataList.Count; i++)
                {
                    var itemData = ItemDataList[i];
                    if (itemData.Position < startPosition)
                        continue;

                    var item = itemData.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    ItemDataList.RemoveAt(i--);
                    Pool.Free(ref itemData);
                    _backpack.IsDirty = true;
                }
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                ItemUtils.SerializeForNetwork(ItemDataList, saveList);
            }

            public void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool)
            {
                foreach (var itemData in ItemDataList)
                {
                    saveList.Add(itemData);
                }
            }

            public void EraseContents()
            {
                foreach (var itemData in ItemDataList)
                {
                    itemData.BeforeErase();
                }

                PoolUtils.ResetItemsAndClear(ItemDataList);

                _backpack.IsDirty = true;
            }

            public void Kill()
            {
                // Intentionally not implemented because there are no actual resources to destroy.
            }

            public VirtualContainerAdapter CopyItemsFrom(List<ItemData> itemDataList)
            {
                var startPosition = PageIndex * MaxCapacity;
                var endPosition = startPosition + Capacity;

                // This assumes the list has already been sorted by item position.
                foreach (var itemData in itemDataList)
                {
                    if (itemData.Position < startPosition)
                        continue;

                    if (itemData.Position >= endPosition)
                        break;

                    ItemDataList.Add(itemData);
                }

                return this;
            }

            private int GetFirstEmptyPosition()
            {
                var nextPossiblePosition = 0;

                for (var i = 0; i < ItemDataList.Count; i++)
                {
                    var itemData = ItemDataList[i];
                    if (itemData.Position > nextPossiblePosition)
                        return i;

                    nextPossiblePosition++;
                }

                return nextPossiblePosition;
            }

            public bool TryDepositItem(Item item)
            {
                var firstEmptyPosition = GetFirstEmptyPosition();
                if (firstEmptyPosition >= Capacity)
                    return false;

                if (!_backpack.ShouldAcceptItem(item, null))
                    return false;

                var itemData = Pool.Get<ItemData>().Setup(item, firstEmptyPosition);
                ItemDataList.Add(itemData);

                item.RemoveFromContainer();
                item.Remove();

                _backpack.IsDirty = true;
                return true;
            }
        }

        private class ItemContainerAdapter : IContainerAdapter
        {
            public int PageIndex { get; private set; }
            public int Capacity
            {
                get { return ItemContainer.capacity; }
                set { ItemContainer.capacity = value; }
            }
            public ItemContainer ItemContainer { get; private set; }
            public bool HasItems => ItemContainer.itemList.Count > 0;

            private Backpack _backpack;

            private Action _onDirty;
            private Func<Item, int, bool> _canAcceptItem;

            private Backpacks _plugin => _backpack.Plugin;
            private Configuration _config => _plugin._config;

            public ItemContainerAdapter()
            {
                _onDirty = () => _backpack.IsDirty = true;
                _canAcceptItem = (item, amount) =>
                {
                    // Explicitly track hook time so server owners can be informed of the cost.
                    _plugin.TrackStart();
                    var result = _backpack.ShouldAcceptItem(item, ItemContainer);
                    _plugin.TrackEnd();
                    return result;
                };
            }

            public ItemContainerAdapter Setup(Backpack backpack, int pageIndex, ItemContainer container)
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::Setup | PageIndex: {pageIndex.ToString()} | Capacity: {container.capacity.ToString()}");
                #endif

                PageIndex = pageIndex;
                ItemContainer = container;
                _backpack = backpack;

                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::EnterPool | PageIndex: {PageIndex.ToString()} | Capacity: {Capacity.ToString()} | {PoolUtils.GetStats<ItemContainerAdapter>()}");
                #endif

                PageIndex = 0;
                ItemContainer = null;
                _backpack = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::LeavePool | {PoolUtils.GetStats<ItemContainerAdapter>()}");
                #endif
            }

            public ItemContainerAdapter AddDelegates()
            {
                // Add delegates only after filling the container initially to avoid marking the container as dirty
                // before any changes have been made, and avoids unnecessary CanBackpackAcceptItem hook calls.
                ItemContainer.onDirty += _onDirty;
                ItemContainer.canAcceptItem = _canAcceptItem;
                return this;
            }

            public void SortByPosition()
            {
                ItemContainer.itemList.Sort((a, b) => a.position.CompareTo(b.position));
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.CountItems(ItemContainer.itemList, ref itemQuery);
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.SumItems(ItemContainer.itemList, ref itemQuery);
            }

            public int TakeItems(List<Item> collect, ref ItemQuery itemQuery, int amount)
            {
                return ItemUtils.TakeItems(ItemContainer.itemList, collect, ref itemQuery, amount);
            }

            public void ReclaimFractionForSoftcore(List<Item> collect, float fraction)
            {
                var itemList = ItemContainer.itemList;

                // For some reason, the vanilla reclaim logic doesn't take the last item.
                if (itemList.Count <= 1)
                    return;

                var numToTake = Mathf.Ceil(itemList.Count * fraction);

                for (var i = 0; i < numToTake; i++)
                {
                    var indexToTake = UnityEngine.Random.Range(0, itemList.Count);
                    var itemToTake = itemList[indexToTake];
                    if (itemToTake.amount > 1)
                    {
                        // Prefer taking a smaller stack if possible (vanilla behavior).
                        foreach (var item in itemList)
                        {
                            if (item.info != itemToTake.info)
                                continue;

                            if (item.amount >= itemToTake.amount)
                                continue;

                            itemToTake = item;
                        }
                    }

                    collect.Add(itemToTake);
                    itemToTake.RemoveFromContainer();
                }
            }

            public void TakeRestrictedItems(List<Item> collect)
            {
                for (var i = ItemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = ItemContainer.itemList[i];
                    if (!_config.IsRestrictedItem(item))
                        continue;

                    collect.Add(item);
                    item.RemoveFromContainer();
                }
            }

            public void TakeAllItems(List<Item> collect, int startPosition = 0)
            {
                SortByPosition();

                for (var i = 0; i < ItemContainer.itemList.Count; i++)
                {
                    var item = ItemContainer.itemList[i];
                    if (item.position < startPosition)
                        continue;

                    collect.Add(item);
                    item.RemoveFromContainer();
                    i--;
                }
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                ItemUtils.SerializeForNetwork(ItemContainer.itemList, saveList);
            }

            public void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool)
            {
                var positionOffset = PageIndex * MaxCapacity;

                foreach (var item in ItemContainer.itemList)
                {
                    var itemData = Pool.Get<ItemData>().Setup(item, positionOffset);
                    saveList.Add(itemData);
                    itemsToReleaseToPool.Add(itemData);
                }
            }

            public void EraseContents()
            {
                for (var i = ItemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = ItemContainer.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }

            public void Kill()
            {
                if (ItemContainer == null || ItemContainer.uid == 0)
                    return;

                foreach (var item in ItemContainer.itemList)
                {
                    // Disassociate entities so they aren't killed.
                    DisassociateEntity(item);
                }

                ItemContainer.Kill();
            }

            public bool TryDepositItem(Item item)
            {
                return item.MoveToContainer(ItemContainer);
            }

            public ItemContainerAdapter CopyItemsFrom(List<ItemData> itemDataList)
            {
                foreach (var itemData in itemDataList)
                {
                    var item = itemData.ToItem();
                    if (item == null)
                        continue;

                    if (!item.MoveToContainer(ItemContainer, item.position)
                        && item.MoveToContainer(ItemContainer))
                    {
                        LogError($"Failed to move item into backpack: {item.amount.ToString()} {item.info.shortname} (skin: {item.skin.ToString()})");
                        item.Remove();
                    }
                }

                return this;
            }

            private void DisassociateEntity(Item item)
            {
                if (item.instanceData != null)
                {
                    if (item.instanceData.subEntity != 0)
                    {
                        var associatedEntity = BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) as BaseEntity;
                        if (associatedEntity != null && associatedEntity.HasParent())
                        {
                            // Disable networking of the entity while it has no parent to reduce unnecessary server load.
                            associatedEntity.limitNetworking = true;

                            // Unparent the associated entity so it's not killed when its parent is.
                            // For example, a CassetteRecorder would normally kill its child Cassette.
                            associatedEntity.SetParent(null);
                        }
                    }

                    // If the item has an associated entity (e.g., photo, sign, cassette), the id will already have been saved.
                    // Forget about the entity when killing the item so that the entity will persist.
                    // When the backpack item is recreated later, this property will set from the data file so that the item can be reassociated.
                    item.instanceData.subEntity = 0;
                }

                if (item.contents != null)
                {
                    foreach (var childItem in item.contents.itemList)
                    {
                        DisassociateEntity(childItem);
                    }
                }
            }
        }

        /// <summary>
        /// A collection of IContainerAdapters which may contain null entries.
        ///
        /// The underlying array may be enlarged but not shrunk via the Resize method.
        ///
        /// When enumerating via foreach, null entries are skipped, and enumeration stops at Count.
        /// </summary>
        private class ContainerAdapterCollection : IEnumerable<IContainerAdapter>
        {
            private class ContainerAdapterEnumerator : IEnumerator<IContainerAdapter>
            {
                public bool InUse => _position >= 0;
                private int _position = -1;
                private ContainerAdapterCollection _adapterCollection;

                public ContainerAdapterEnumerator(ContainerAdapterCollection adapterCollection)
                {
                    _adapterCollection = adapterCollection;
                }

                public bool MoveNext()
                {
                    while (++_position < _adapterCollection.Count)
                    {
                        if (_adapterCollection[_position] != null)
                            return true;
                    }

                    return false;
                }

                public void Reset()
                {
                    throw new NotImplementedException();
                }

                public IContainerAdapter Current => _adapterCollection[_position];

                object IEnumerator.Current => Current;

                public void Dispose()
                {
                    _position = -1;
                }
            }

            public int Count { get; private set; }
            private IContainerAdapter[] _containerAdapters;
            private ContainerAdapterEnumerator _enumerator;

            public ContainerAdapterCollection(int size)
            {
                Resize(size);
                _enumerator = new ContainerAdapterEnumerator(this);
            }

            public void RemoveAt(int index)
            {
                this[index] = null;
            }

            public IContainerAdapter this[int i]
            {
                get
                {
                    if (i >= Count)
                        throw new IndexOutOfRangeException($"Index {i} was outside the bounds of the collection of size {Count}");

                    return _containerAdapters[i];
                }
                set
                {
                    if (i >= Count)
                        throw new IndexOutOfRangeException($"Index {i} was outside the bounds of the collection of size {Count}");

                    _containerAdapters[i] = value;
                }
            }

            public IEnumerator<IContainerAdapter> GetEnumerator()
            {
                if (_enumerator.InUse)
                    throw new InvalidOperationException($"{nameof(ContainerAdapterEnumerator)} was not disposed after previous use");

                return _enumerator;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public void Resize(int newSize)
            {
                if (newSize == Count)
                    return;

                if (newSize > Count)
                {
                    Array.Resize(ref _containerAdapters, newSize);
                }
                else
                {
                    for (var i = Count; i < _containerAdapters.Length; i++)
                    {
                        if (_containerAdapters[i] != null)
                            throw new InvalidOperationException($"ContainerAdapterCollection cannot be shrunk from {Count} to {newSize} because there is an existing container adapter at index {i}");
                    }
                }

                Count = newSize;
            }

            public void ResetPooledItemsAndClear()
            {
                PoolUtils.ResetItemsAndClear(_containerAdapters);
                Count = 0;
            }
        }

        #endregion

        #region Backpack

        private struct BackpackCapacity
        {
            public static int CalculatePageCapacity(int totalCapacity, int pageIndex)
            {
                if (pageIndex < 0)
                    throw new ArgumentOutOfRangeException($"Page cannot be negative: {pageIndex}.");

                var numPages = CalculatePageCountForCapacity(totalCapacity);
                var lastPageIndex = numPages - 1;

                if (pageIndex > lastPageIndex)
                    throw new ArgumentOutOfRangeException($"Page {pageIndex} cannot exceed {lastPageIndex}");

                return pageIndex < lastPageIndex
                    ? MaxCapacity
                    : totalCapacity - MaxCapacity * lastPageIndex;
            }

            public static bool operator >(BackpackCapacity a, BackpackCapacity b) => a.Capacity > b.Capacity;
            public static bool operator <(BackpackCapacity a, BackpackCapacity b) => a.Capacity < b.Capacity;

            public static bool operator >=(BackpackCapacity a, BackpackCapacity b) => a.Capacity >= b.Capacity;
            public static bool operator <=(BackpackCapacity a, BackpackCapacity b) => a.Capacity <= b.Capacity;

            private static int CalculatePageCountForCapacity(int capacity)
            {
                return 1 + (capacity - 1) / MaxCapacity;
            }

            public int Capacity
            {
                get
                {
                    return _capacity;
                }
                set
                {
                    _capacity = value;
                    PageCount = CalculatePageCountForCapacity(value);
                }
            }
            public int PageCount { get; private set; }
            public int LastPage => PageCount - 1;
            public int LastPageCapacity => CapacityForPage(LastPage);
            public int CapacityForPage(int pageIndex) => CalculatePageCapacity(Capacity, pageIndex);
            public int ClampPage(int pageIndex) => Mathf.Clamp(pageIndex, 0, LastPage);

            private int _capacity;
        }

        [JsonObject(MemberSerialization.OptIn)]
        [JsonConverter(typeof(PoolConverter<Backpack>))]
        private class Backpack : Pool.IPooled
        {
            private static int CalculatePageIndexForItemPosition(int position)
            {
                return position / MaxCapacity;
            }

            [JsonProperty("OwnerID", Order = 0)]
            public ulong OwnerId { get; set; }

            [JsonProperty("WipeNumber", Order = 1, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int WipeNumber;

            [JsonProperty("Items", Order = 2)]
            private List<ItemData> ItemDataCollection = new List<ItemData>();

            public Backpacks Plugin;
            public BackpackNetworkController NetworkController { get; private set; }
            public bool AllowedCapacityNeedsRefresh = true;
            public bool RespectsItemRestrictionsNeedsRefresh = true;
            public string OwnerIdString;
            public bool IsDirty;

            private BackpackCapacity ActualCapacity;
            private BackpackCapacity _allowedCapacity;

            private bool _respectsItemRestrictions;
            private bool _processedRestrictedItems;
            private DynamicConfigFile _dataFile;
            private StorageContainer _storageContainer;
            private ContainerAdapterCollection _containerAdapters;
            private readonly List<BasePlayer> _looters = new List<BasePlayer>();

            public bool HasLooters => _looters.Count > 0;
            private Configuration _config => Plugin._config;
            private BackpackManager _backpackManager => Plugin._backpackManager;

            private BackpackCapacity AllowedCapacity
            {
                get
                {
                    if (AllowedCapacityNeedsRefresh)
                    {
                        _allowedCapacity.Capacity = Math.Max(MinCapacity, Plugin._backpackCapacityManager.DetermineCapacity(OwnerIdString));
                        AllowedCapacityNeedsRefresh = false;
                    }

                    return _allowedCapacity;
                }

                set
                {
                    _allowedCapacity = value;
                }
            }

            public bool RespectsItemRestrictions
            {
                get
                {
                    if (RespectsItemRestrictionsNeedsRefresh)
                    {
                        var shouldRespect = !Plugin.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission);
                        if (shouldRespect && !_respectsItemRestrictions)
                        {
                            // Re-evaluate existing items when the backpack is next opened.
                            _processedRestrictedItems = false;
                        }

                        _respectsItemRestrictions = shouldRespect;
                        RespectsItemRestrictionsNeedsRefresh = false;
                    }

                    return _respectsItemRestrictions;
                }
            }

            private bool HasItems
            {
                get
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        if (containerAdapter.HasItems)
                            return true;
                    }
                    return false;
                }
            }

            public void Setup(Backpacks plugin, ulong ownerId, DynamicConfigFile dataFile)
            {
                #if DEBUG_POOLING
                LogDebug($"Backpack::Setup | OwnerId: {ownerId.ToString()}");
                #endif

                Plugin = plugin;
                OwnerId = ownerId;
                OwnerIdString = ownerId.ToString();
                _dataFile = dataFile;

                if (NetworkController == null)
                {
                    NetworkController = BackpackNetworkController.Create();
                }

                SetupItemsAndContainers();
            }

            private void SetupItemsAndContainers()
            {
                // Sort the items so it's easier to partition the list for multiple pages.
                ItemDataCollection.Sort((a, b) => a.Position.CompareTo(b.Position));

                // Allow the backpack to start beyond the allowed capacity.
                // Overflowing items will be handled when the backpack is opened by its owner.
                var highestUsedPosition = ItemDataCollection.LastOrDefault()?.Position ?? 0;
                ActualCapacity.Capacity = Math.Max(_allowedCapacity.Capacity, highestUsedPosition + 1);

                var pageCount = ActualCapacity.PageCount;
                if (_containerAdapters == null)
                {
                    _containerAdapters = new ContainerAdapterCollection(pageCount);
                }
                else
                {
                    _containerAdapters.Resize(pageCount);
                }

                // Forget about associated entities from previous wipes.
                if (WipeNumber != Plugin._wipeNumber)
                {
                    foreach (var itemData in ItemDataCollection)
                    {
                        itemData.DissociateEntity();
                    }
                }

                WipeNumber = Plugin._wipeNumber;

                CreateContainerAdapters();
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"Backpack::EnterPool | OwnerId: {OwnerIdString} | {PoolUtils.GetStats<Backpack>()}");
                #endif

                OwnerId = 0;
                WipeNumber = 0;
                if (ItemDataCollection != null)
                {
                    PoolUtils.ResetItemsAndClear(ItemDataCollection);
                }
                Plugin = null;

                // Don't remove the NetworkController. Will reuse it for the next Backpack owner.
                NetworkController?.UnsubscribeAll();
                AllowedCapacityNeedsRefresh = true;
                RespectsItemRestrictionsNeedsRefresh = true;
                OwnerIdString = null;
                IsDirty = false;
                ActualCapacity = default(BackpackCapacity);
                _allowedCapacity = default(BackpackCapacity);
                _respectsItemRestrictions = true;
                _processedRestrictedItems = false;
                _dataFile = null;
                _storageContainer = null;
                _containerAdapters?.ResetPooledItemsAndClear();
                _looters.Clear();
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"LeavePool | {PoolUtils.GetStats<Backpack>()}");
                #endif
            }

            public int GetPageIndexForContainer(ItemContainer container)
            {
                return GetAdapterForContainer(container)?.PageIndex ?? -1;
            }

            public ItemContainerAdapter EnsureItemContainerAdapter(int pageIndex)
            {
                var containerAdapter = EnsurePage(pageIndex, preferRealContainer: true);
                return containerAdapter as ItemContainerAdapter
                       ?? UpgradeToItemContainer(containerAdapter as VirtualContainerAdapter);
            }

            public int GetAllowedPageCapacityForLooter(ulong looterId, int desiredPageIndex)
            {
                return GetAllowedCapacityForLooter(looterId).CapacityForPage(desiredPageIndex);
            }

            public int DetermineInitialPageForLooter(ulong looterId, int desiredPageIndex, bool forward, out int pageCapacity)
            {
                var allowedCapacity = GetAllowedCapacityForLooter(looterId);

                if (desiredPageIndex == -1)
                {
                    desiredPageIndex = forward ? 0 : allowedCapacity.LastPage;
                }

                desiredPageIndex = allowedCapacity.ClampPage(desiredPageIndex);
                pageCapacity = allowedCapacity.Capacity;
                return desiredPageIndex;
            }

            public int DetermineNextPageIndexForLooter(ulong looterId, int currentPageIndex, int desiredPageIndex, bool forward, bool wrapAround, bool requireContents)
            {
                var allowedCapacity = GetAllowedCapacityForLooter(looterId);

                if (desiredPageIndex >= 0)
                    return Math.Min(desiredPageIndex, allowedCapacity.LastPage);

                if (forward)
                {
                    for (var i = currentPageIndex + 1; i < allowedCapacity.PageCount; i++)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (!requireContents || (containerAdapter?.HasItems ?? false))
                            return i;
                    }

                    if (wrapAround)
                    {
                        for (var i = 0; i < currentPageIndex; i++)
                        {
                            var containerAdapter = _containerAdapters[i];
                            if (!requireContents || (containerAdapter?.HasItems ?? false))
                                return i;
                        }
                    }
                }
                else
                {
                    // Searching backward.
                    for (var i = currentPageIndex - 1; i >= 0; i--)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (!requireContents || (containerAdapter?.HasItems ?? false))
                            return i;
                    }

                    if (wrapAround)
                    {
                        for (var i = allowedCapacity.LastPage; i > currentPageIndex; i++)
                        {
                            var containerAdapter = _containerAdapters[i];
                            if (!requireContents || (containerAdapter?.HasItems ?? false))
                                return i;
                        }
                    }
                }

                return currentPageIndex;
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                var count = 0;
                foreach (var containerAdapter in _containerAdapters)
                {
                    count += containerAdapter.CountItems(ref itemQuery);
                }
                return count;
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                var sum = 0;
                foreach (var containerAdapter in _containerAdapters)
                {
                    sum += containerAdapter.SumItems(ref itemQuery);
                }
                return sum;
            }

            public int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                var amountTaken = 0;
                foreach (var containerAdapter in _containerAdapters)
                {
                    var amountToTake = amount - amountTaken;
                    if (amountToTake <= 0)
                        break;

                    amountTaken += containerAdapter.TakeItems(collect, ref itemQuery, amountToTake);
                }
                return amountTaken;
            }

            public bool TryDepositItem(Item item)
            {
                // When overflowing, don't allow items to be added.
                if (ActualCapacity > AllowedCapacity)
                    return false;

                for (var i = 0; i < AllowedCapacity.PageCount; i++)
                {
                    var containerAdapter = EnsurePage(i);
                    if (!containerAdapter.TryDepositItem(item))
                        continue;

                    return true;
                }

                return false;
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    containerAdapter.SerializeForNetwork(saveList);
                }
            }

            public IPlayer FindOwnerPlayer() => Plugin.covalence.Players.FindPlayerById(OwnerIdString);

            public bool ShouldAcceptItem(Item item, ItemContainer container)
            {
                if (_config.ItemRestrictionEnabled
                    && RespectsItemRestrictions
                    && _config.IsRestrictedItem(item))
                    return false;

                var hookResult = ExposedHooks.CanBackpackAcceptItem(Plugin, OwnerId, container, item);
                if (hookResult is bool && (bool)hookResult == false)
                    return false;

                return true;
            }

            public ItemContainer GetContainer(bool ensureContainer = false)
            {
                if (ensureContainer)
                    return EnsureItemContainerAdapter(0).ItemContainer;

                return (EnsurePage(0) as ItemContainerAdapter)?.ItemContainer;
            }

            public bool TryOpen(BasePlayer looter, int pageIndex = -1)
            {
                if (!Plugin.VerifyCanOpenBackpack(looter, OwnerId))
                    return false;

                EnlargeIfNeeded();

                pageIndex = GetAllowedCapacityForLooter(looter.userID).ClampPage(pageIndex);
                var itemContainerAdapter = EnsureItemContainerAdapter(pageIndex);

                NetworkController.Subscribe(looter);

                // Some operations are only appropriate for the owner (not for admins viewing the backpack).
                if (looter.userID == OwnerId)
                {
                    EjectRestrictedItemsIfNeeded(looter);
                    ShrinkIfNeededAndEjectOverflowingItems(looter);
                }

                if (!_looters.Contains(looter))
                {
                    _looters.Add(looter);
                }

                StartLooting(looter, itemContainerAdapter.ItemContainer, _storageContainer);
                ExposedHooks.OnBackpackOpened(Plugin, looter, OwnerId, itemContainerAdapter.ItemContainer);
                return true;
            }

            public void SwitchToPage(BasePlayer looter, int pageIndex)
            {
                var itemContainer = EnsureItemContainerAdapter(pageIndex).ItemContainer;
                var playerLoot = looter.inventory.loot;
                foreach (var container in playerLoot.containers)
                {
                    container.onDirty -= playerLoot.MarkDirty;
                }

                playerLoot.containers.Clear();
                Interface.CallHook("OnLootEntityEnd", looter, itemContainer.entityOwner);
                Interface.CallHook("OnLootEntity", looter, itemContainer.entityOwner);
                playerLoot.AddContainer(itemContainer);
                playerLoot.SendImmediate();
                ExposedHooks.OnBackpackOpened(Plugin, looter, OwnerId, itemContainer);
            }

            public void OnClosed(BasePlayer looter)
            {
                _looters.Remove(looter);

                // Clean up the subscription immediately if admin stopped looting.
                // This avoids having to clean up the admin subscriptions some other way which would add complexity.
                if (looter.userID != OwnerId)
                {
                    NetworkController?.Unsubscribe(looter);
                }
            }

            public DroppedItemContainer Drop(Vector3 position, List<DroppedItemContainer> collectContainers = null)
            {
                if (!HasItems)
                    return null;

                var hookResult = ExposedHooks.CanDropBackpack(Plugin, OwnerId, position);
                if (hookResult is bool && (bool)hookResult == false)
                    return null;

                ForceCloseAllLooters();
                ReclaimItemsForSoftcore();

                // Check again since the items may have all been reclaimed for Softcore.
                if (!HasItems)
                    return null;

                DroppedItemContainer firstContainer = null;

                using (var itemList = DisposableList<Item>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        if (!containerAdapter.HasItems)
                            continue;

                        containerAdapter.TakeAllItems(itemList);
                        var droppedItemContainer = SpawnDroppedBackpack(position, containerAdapter.Capacity, itemList);
                        if (droppedItemContainer == null)
                            break;

                        itemList.Clear();

                        if ((object)firstContainer == null)
                        {
                            firstContainer = droppedItemContainer;
                        }

                        collectContainers?.Add(droppedItemContainer);
                    }

                    if (itemList.Count > 0)
                    {
                        foreach (var item in itemList)
                        {
                            item.Drop(position, UnityEngine.Random.insideUnitSphere, Quaternion.identity);
                        }
                    }
                }

                return firstContainer;
            }

            public void EraseContents(bool force = false)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                if (!HasItems)
                    return;

                if (!force)
                {
                    var hookResult = ExposedHooks.CanEraseBackpack(Plugin, OwnerId);
                    if (hookResult is bool && (bool)hookResult == false)
                        return;
                }

                foreach (var containerAdapter in _containerAdapters)
                {
                    containerAdapter.EraseContents();
                }
            }

            public bool SaveIfChanged()
            {
                if (!IsDirty)
                    return false;

                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Save | {OwnerIdString} | Frame: {Time.frameCount.ToString()}");
                #endif

                using (var itemsToReleaseToPool = DisposableList<ItemData>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.SerializeTo(ItemDataCollection, itemsToReleaseToPool);
                    }

                    _dataFile.WriteObject(this);
                    IsDirty = false;

                    // After saving, unused ItemData instances can be pooled.
                    PoolUtils.ResetItemsAndClear(itemsToReleaseToPool);
                }

                // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                ItemDataCollection.Clear();

                return true;
            }

            public int FetchItems(BasePlayer player, ref ItemQuery itemQuery, int desiredAmount)
            {
                using (var collect = DisposableList<Item>.Get())
                {
                    var amountTaken = TakeItems(ref itemQuery, desiredAmount, collect);
                    foreach (var item in collect)
                    {
                        player.GiveItem(item);
                    }
                    return amountTaken;
                }
            }

            public void Kill()
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Kill | OwnerId: {OwnerIdString} | Frame: {Time.frameCount.ToString()}");
                #endif

                ForceCloseAllLooters();

                foreach (var containerAdapter in _containerAdapters)
                {
                    var adapter = containerAdapter;
                    KillContainerAdapter(ref adapter);
                }

                if (_storageContainer != null && !_storageContainer.IsDestroyed)
                {
                    // Note: The ItemContainer will already be Kill()'d by this point, but that's OK.
                    _storageContainer.Kill();
                }
            }

            public string SerializeContentsAsJson()
            {
                using (var itemsToReleaseToPool = DisposableList<ItemData>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.SerializeTo(ItemDataCollection, itemsToReleaseToPool);
                    }

                    var json = JsonConvert.SerializeObject(ItemDataCollection);

                    // After saving, unused ItemData instances can be pooled.
                    PoolUtils.ResetItemsAndClear(itemsToReleaseToPool);

                    // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                    ItemDataCollection.Clear();

                    return json;
                }
            }

            public void WriteContentsFromJson(string json)
            {
                var itemDataList = JsonConvert.DeserializeObject<List<ItemData>>(json);

                Kill();

                foreach (var itemData in itemDataList)
                {
                    ItemDataCollection.Add(itemData);
                }

                SetupItemsAndContainers();

                IsDirty = true;
                SaveIfChanged();
            }

            private void CreateContainerAdapters()
            {
                var previousPageIndex = -1;

                // This assumes the collection has been sorted by item position.
                foreach (var itemData in ItemDataCollection)
                {
                    var pageIndex = CalculatePageIndexForItemPosition(itemData.Position);
                    if (pageIndex < previousPageIndex)
                        throw new InvalidOperationException("Found an item for an earlier page while setting up a virtual container. This should not happen.");

                    // Skip items for the previously created page, since creating the page would have copied all items.
                    if (pageIndex == previousPageIndex)
                        continue;

                    // Create an adapter for the page, copying all items.
                    _containerAdapters[pageIndex] = CreateVirtualContainerAdapter(pageIndex)
                        .CopyItemsFrom(ItemDataCollection);

                    previousPageIndex = pageIndex;
                }

                // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                ItemDataCollection.Clear();
            }

            private VirtualContainerAdapter CreateVirtualContainerAdapter(int pageIndex)
            {
                return Pool.Get<VirtualContainerAdapter>().Setup(this, pageIndex, ActualCapacity.CapacityForPage(pageIndex));
            }

            private ItemContainerAdapter CreateItemContainerAdapter(int pageIndex)
            {
                var container = CreateContainerForPage(pageIndex, ActualCapacity.CapacityForPage(pageIndex));
                return Pool.Get<ItemContainerAdapter>().Setup(this, pageIndex, container);
            }

            private ItemContainerAdapter UpgradeToItemContainer(VirtualContainerAdapter virtualContainerAdapter)
            {
                // Must cache the page index since it will be reset when pooled.
                var pageIndex = virtualContainerAdapter.PageIndex;
                var itemContainerAdapter = CreateItemContainerAdapter(pageIndex)
                    .CopyItemsFrom(virtualContainerAdapter.ItemDataList)
                    .AddDelegates();

                Pool.Free(ref virtualContainerAdapter);

                _containerAdapters[pageIndex] = itemContainerAdapter;
                return itemContainerAdapter;
            }

            private void EjectRestrictedItemsIfNeeded(BasePlayer receiver)
            {
                if (!Plugin._config.ItemRestrictionEnabled)
                    return;

                // Some backpacks may ignore item restrictions due to permissions.
                if (!RespectsItemRestrictions)
                    return;

                // Optimization: Avoid processing item restrictions every time the backpack is opened.
                if (_processedRestrictedItems)
                    return;

                using (var ejectedItems = DisposableList<Item>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.TakeRestrictedItems(ejectedItems);
                    }

                    if (ejectedItems.Count > 0)
                    {
                        foreach (var item in ejectedItems)
                        {
                            receiver.GiveItem(item);
                        }

                        receiver.ChatMessage(Plugin.GetMessage(receiver, "Blacklisted Items Removed"));
                    }
                }

                _processedRestrictedItems = true;
            }

            private void ShrinkIfNeededAndEjectOverflowingItems(BasePlayer overflowRecipient)
            {
                var allowedCapacity = AllowedCapacity;
                if (ActualCapacity <= allowedCapacity)
                    return;

                var allowedLastPageCapacity = allowedCapacity.LastPageCapacity;

                var itemsDroppedOrGivenToPlayer = 0;

                using (var overflowingItems = DisposableList<Item>.Get())
                {
                    var lastAllowedContainerAdapter = _containerAdapters[allowedCapacity.LastPage];
                    if (lastAllowedContainerAdapter != null)
                    {
                        lastAllowedContainerAdapter.TakeAllItems(overflowingItems, allowedLastPageCapacity);
                        lastAllowedContainerAdapter.Capacity = allowedLastPageCapacity;

                        if (allowedLastPageCapacity > 0)
                        {
                            // Try to give the items to the original page first.
                            var lastAllowedItemContainerAdapter = EnsureItemContainerAdapter(allowedCapacity.LastPage);

                            for (var i = 0; i < overflowingItems.Count; i++)
                            {
                                if (overflowingItems[i].MoveToContainer(lastAllowedItemContainerAdapter.ItemContainer))
                                {
                                    overflowingItems.RemoveAt(i--);
                                }
                            }
                        }
                    }

                    for (var i = allowedCapacity.PageCount; i < ActualCapacity.PageCount; i++)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (containerAdapter == null)
                            continue;

                        containerAdapter.TakeAllItems(overflowingItems);
                        KillContainerAdapter(ref containerAdapter);
                    }

                    foreach (var item in overflowingItems)
                    {
                        var wasItemAddedToBackpack = false;

                        for (var i = 0; i < allowedCapacity.PageCount; i++)
                        {
                            // Simplification: Make all potential destination containers real containers.
                            var itemContainerAdapter = EnsureItemContainerAdapter(i);
                            if (itemContainerAdapter.TryDepositItem(item))
                            {
                                wasItemAddedToBackpack = true;
                                break;
                            }
                        }

                        if (!wasItemAddedToBackpack)
                        {
                            overflowRecipient.GiveItem(item);
                            itemsDroppedOrGivenToPlayer++;
                        }
                    }
                }

                if (itemsDroppedOrGivenToPlayer > 0)
                {
                    overflowRecipient.ChatMessage(Plugin.GetMessage(overflowRecipient, "Backpack Over Capacity"));
                }

                ActualCapacity = AllowedCapacity;
            }

            private void SetupContainer(ItemContainer container)
            {
                _backpackManager.RegisterContainer(container, this);
            }

            private ItemContainer CreateContainerForPage(int page, int capacity)
            {
                if ((object)_storageContainer == null || _storageContainer.IsDestroyed)
                {
                    _storageContainer = SpawnStorageContainer(0);
                    if ((object)_storageContainer == null)
                        return null;
                }

                if (page == 0)
                {
                    _storageContainer.inventory.capacity = capacity;
                    SetupContainer(_storageContainer.inventory);
                    return _storageContainer.inventory;
                }

                var itemContainer = CreateItemContainer(capacity, _storageContainer);
                SetupContainer(itemContainer);
                return itemContainer;
            }

            private ItemContainerAdapter GetAdapterForContainer(ItemContainer container)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    var itemContainerAdapter = containerAdapter as ItemContainerAdapter;
                    if (itemContainerAdapter?.ItemContainer != container)
                        continue;

                    return itemContainerAdapter;
                }

                return null;
            }

            private IContainerAdapter EnsurePage(int pageIndex, bool preferRealContainer = false)
            {
                var containerAdapter = _containerAdapters[pageIndex];
                if (containerAdapter == null)
                {
                    if (preferRealContainer)
                    {
                        containerAdapter = CreateItemContainerAdapter(pageIndex).AddDelegates();
                    }
                    else
                    {
                        containerAdapter = CreateVirtualContainerAdapter(pageIndex);
                    }

                    _containerAdapters[pageIndex] = containerAdapter;
                }

                return containerAdapter;
            }

            private BackpackCapacity GetAllowedCapacityForLooter(ulong looterId)
            {
                return looterId == OwnerId ? AllowedCapacity : ActualCapacity;
            }

            private DroppedItemContainer SpawnDroppedBackpack(Vector3 position, int capacity, List<Item> itemList)
            {
                var entity = GameManager.server.CreateEntity(DroppedBackpackPrefab, position);
                if (entity == null)
                {
                    LogError($"Failed to create entity: {DroppedBackpackPrefab}");
                    return null;
                }

                var droppedItemContainer = entity as DroppedItemContainer;
                if (droppedItemContainer == null)
                {
                    LogError($"Entity is not an instance of DroppedItemContainer: {DroppedBackpackPrefab}");
                    return null;
                }

                droppedItemContainer.gameObject.AddComponent<NoRagdollCollision>();

                droppedItemContainer.lootPanelName = ResizableLootPanelName;
                droppedItemContainer.playerName = $"{FindOwnerPlayer()?.Name ?? "Somebody"}'s Backpack";
                droppedItemContainer.playerSteamID = OwnerId;

                droppedItemContainer.inventory = new ItemContainer();
                droppedItemContainer.inventory.ServerInitialize(null, capacity);
                droppedItemContainer.inventory.GiveUID();
                droppedItemContainer.inventory.entityOwner = droppedItemContainer;
                droppedItemContainer.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

                foreach (var item in itemList)
                {
                    if (!item.MoveToContainer(droppedItemContainer.inventory))
                    {
                        item.Remove();
                    }
                }

                droppedItemContainer.Spawn();
                droppedItemContainer.ResetRemovalTime(Math.Max(Plugin._config.MinimumDespawnTime, droppedItemContainer.CalculateRemovalTime()));

                return droppedItemContainer;
            }

            private void EnlargeIfNeeded()
            {
                var allowedCapacity = AllowedCapacity;
                if (ActualCapacity >= allowedCapacity)
                    return;

                var allowedPageCount = allowedCapacity.PageCount;
                if (_containerAdapters.Count < allowedPageCount)
                {
                    _containerAdapters.Resize(allowedPageCount);
                }

                for (var i = 0; i < allowedPageCount; i++)
                {
                    var containerAdapter = _containerAdapters[i];
                    if (containerAdapter == null)
                        continue;

                    var allowedPageCapacity = allowedCapacity.CapacityForPage(i);
                    if (containerAdapter.Capacity < allowedPageCapacity)
                    {
                        containerAdapter.Capacity = allowedPageCapacity;
                    }
                }

                ActualCapacity = AllowedCapacity;
            }

            private void KillContainerAdapter<T>(ref T containerAdapter) where T : class, IContainerAdapter
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::KillContainerAdapter({typeof(T).Name}) | OwnerId: {OwnerIdString} | PageIndex: {containerAdapter.PageIndex.ToString()} | Capacity: {containerAdapter.Capacity.ToString()} ");
                #endif

                var itemContainerAdapter = containerAdapter as ItemContainerAdapter;
                if (itemContainerAdapter != null)
                {
                    _backpackManager.UnregisterContainer(itemContainerAdapter.ItemContainer);
                }

                containerAdapter.Kill();
                _containerAdapters.RemoveAt(containerAdapter.PageIndex);
                Pool.Free(ref containerAdapter);
            }

            private void ForceCloseLooter(BasePlayer looter)
            {
                looter.inventory.loot.Clear();
                looter.inventory.loot.MarkDirty();
                looter.inventory.loot.SendImmediate();

                OnClosed(looter);
            }

            private void ForceCloseAllLooters()
            {
                for (var i = _looters.Count - 1; i >= 0; i--)
                {
                    ForceCloseLooter(_looters[i]);
                }
            }

            private StorageContainer SpawnStorageContainer(int capacity)
            {
                var storageEntity = GameManager.server.CreateEntity(CoffinPrefab, new Vector3(0, -500, 0));
                if (storageEntity == null)
                    return null;

                var containerEntity = storageEntity as StorageContainer;
                if (containerEntity == null)
                {
                    UnityEngine.Object.Destroy(storageEntity.gameObject);
                    return null;
                }

                containerEntity.SetFlag(BaseEntity.Flags.Disabled, true);

                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<DestroyOnGroundMissing>());
                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<GroundWatch>());

                foreach (var collider in containerEntity.GetComponentsInChildren<Collider>())
                    UnityEngine.Object.DestroyImmediate(collider);

                containerEntity.CancelInvoke(containerEntity.DecayTick);

                BackpackCloseListener.AddToBackpackStorage(Plugin, containerEntity, this);

                containerEntity.baseProtection = Plugin._immortalProtection;
                containerEntity.panelName = ResizableLootPanelName;

                // Temporarily disable networking to prevent initially sending the entity to clients based on the positional network group.
                containerEntity._limitedNetworking = true;

                containerEntity.EnableSaving(false);
                containerEntity.Spawn();

                // Must change the network group after spawning,
                // or else vanilla UpdateNetworkGroup will switch it to a positional network group.
                containerEntity.net.SwitchGroup(NetworkController.NetworkGroup);

                // Re-enable networking now that the entity is in the correct network group.
                containerEntity._limitedNetworking = false;

                containerEntity.inventory.allowedContents = ItemContainer.ContentsType.Generic;
                containerEntity.inventory.capacity = capacity;

                return containerEntity;
            }

            private void ReclaimItemsForSoftcore()
            {
                var softcoreGameMode = BaseGameMode.svActiveGameMode as GameModeSoftcore;
                if ((object)softcoreGameMode == null || (object)ReclaimManager.instance == null)
                    return;

                var reclaimFraction = Plugin._config.Softcore.ReclaimFraction;
                if (reclaimFraction <= 0)
                    return;

                using (var allItemsToReclaim = DisposableList<Item>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.ReclaimFractionForSoftcore(allItemsToReclaim, reclaimFraction);
                    }

                    if (allItemsToReclaim.Count > 0)
                    {
                        // There's a vanilla issue where accessing the reclaim backpack will erase items in the reclaim entry above 32.
                        // So we just add new reclaim entries which can only be accessed at the terminal to avoid this issue.
                        // Additionally, reclaim entries have a max size, so we may need to create multiple.
                        while (allItemsToReclaim.Count > ReclaimEntryMaxSize)
                        {
                            using (var itemsToReclaimForEntry = DisposableList<Item>.Get())
                            {
                                for (var i = 0; i < ReclaimEntryMaxSize; i++)
                                {
                                    itemsToReclaimForEntry.Add(allItemsToReclaim[i]);
                                    allItemsToReclaim.RemoveAt(i);
                                }
                                ReclaimManager.instance.AddPlayerReclaim(OwnerId, itemsToReclaimForEntry);
                            }
                        }

                        ReclaimManager.instance.AddPlayerReclaim(OwnerId, allItemsToReclaim);
                    }
                }
            }
        }

        [JsonConverter(typeof(PoolConverter<ItemData>))]
        public class ItemData : Pool.IPooled
        {
            [JsonProperty("ID")]
            public int ID { get; private set; }

            [JsonProperty("Position")]
            public int Position { get; set; } = -1;

            [JsonProperty("Amount")]
            public int Amount { get; private set; }

            [JsonProperty("IsBlueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private bool IsBlueprint;

            [JsonProperty("BlueprintTarget", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int BlueprintTarget { get; private set; }

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin { get; private set; }

            [JsonProperty("Fuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private float Fuel;

            [JsonProperty("FlameFuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int FlameFuel;

            [JsonProperty("Condition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Condition { get; private set; }

            [JsonProperty("MaxCondition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float MaxCondition { get; private set; } = -1;

            [JsonProperty("Ammo", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int Ammo;

            [JsonProperty("AmmoType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int AmmoType;

            [JsonProperty("DataInt", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt { get; private set; }

            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name { get; private set; }

            [JsonProperty("Text", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private string Text;

            [JsonProperty("Flags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Item.Flag Flags { get; private set; }

            [JsonProperty("AssociatedEntityId", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private uint AssociatedEntityId;

            [JsonProperty("Contents", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(PoolListConverter<ItemData>))]
            public List<ItemData> Contents { get; private set; }

            public ItemData Setup(Item item, int positionOffset = 0)
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::Setup | {item.amount.ToString()} {item.info.shortname}");
                #endif

                ID = item.info.itemid;
                Position = item.position + positionOffset;
                Amount = item.amount;
                IsBlueprint = item.IsBlueprint();
                BlueprintTarget = item.blueprintTarget;
                Skin = item.skin;
                Fuel = item.fuel;
                FlameFuel = item.GetHeldEntity()?.GetComponent<FlameThrower>()?.ammo ?? 0;
                Condition = item.condition;
                MaxCondition = item.maxCondition;
                Ammo = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.contents ?? 0;
                AmmoType = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.ammoType?.itemid ?? 0;
                DataInt = item.instanceData?.dataInt ?? 0;
                Name = item.name;
                Text = item.text;
                Flags = item.flags;
                AssociatedEntityId = item.instanceData?.subEntity ?? 0;

                if (item.contents != null)
                {
                    Contents = Pool.GetList<ItemData>();
                    foreach (var childItem in item.contents.itemList)
                    {
                        Contents.Add(Pool.Get<ItemData>().Setup(childItem));
                    }
                }

                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::EnterPool | {Amount.ToString()} {ItemManager.FindItemDefinition(ID)?.shortname ?? ID.ToString()} | {PoolUtils.GetStats<ItemData>()}");
                #endif

                ID = 0;
                Position = 0;
                Amount = 0;
                IsBlueprint = false;
                BlueprintTarget = 0;
                Skin = 0;
                Fuel = 0;
                FlameFuel = 0;
                Condition = 0;
                MaxCondition = 0;
                Ammo = 0;
                AmmoType = 0;
                DataInt = 0;
                Name = null;
                Text = null;
                Flags = 0;
                AssociatedEntityId = 0;

                if (Contents != null)
                {
                    PoolUtils.ResetItemsAndClear(Contents);
                    var contents = Contents;
                    Pool.FreeList(ref contents);
                    Contents = null;
                }
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::LeavePool | {PoolUtils.GetStats<ItemData>()}");
                #endif
            }

            public void Reduce(int amount)
            {
                Amount -= amount;
            }

            public void DissociateEntity()
            {
                AssociatedEntityId = 0;
            }

            public void BeforeErase()
            {
                if (AssociatedEntityId == 0)
                    return;

                var entity = BaseNetworkable.serverEntities.Find(AssociatedEntityId);
                if (entity == null || entity.IsDestroyed)
                    return;

                entity.Kill();
            }

            public Item ToItem(int amount = -1)
            {
                if (amount == -1)
                {
                    amount = Amount;
                }

                if (amount == 0)
                    return null;

                Item item = ItemManager.CreateByItemID(ID, Amount, Skin);
                if (item == null)
                    return null;

                item.position = Position % MaxCapacity;

                if (IsBlueprint)
                {
                    item.blueprintTarget = BlueprintTarget;
                    return item;
                }

                item.fuel = Fuel;
                item.condition = Condition;

                if (MaxCondition != -1)
                {
                    item.maxCondition = MaxCondition;
                }

                if (Name != null)
                {
                    item.name = Name;
                }

                if (amount == Amount && Contents?.Count > 0)
                {
                    if (item.contents == null)
                    {
                        item.contents = new ItemContainer();
                        item.contents.ServerInitialize(null, Contents.Count);
                        item.contents.GiveUID();
                        item.contents.parent = item;
                    }
                    else
                    {
                        item.contents.capacity = Math.Max(item.contents.capacity, Contents.Count);
                    }

                    foreach (var contentItem in Contents)
                    {
                        var childItem = contentItem.ToItem();
                        if (childItem == null)
                            continue;

                        if (!childItem.MoveToContainer(item.contents, childItem.position)
                            && !childItem.MoveToContainer(item.contents))
                        {
                            childItem.Remove();
                        }
                    }
                }

                item.flags |= Flags;

                BaseProjectile.Magazine magazine = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine;
                FlameThrower flameThrower = item.GetHeldEntity()?.GetComponent<FlameThrower>();

                if (magazine != null)
                {
                    magazine.contents = Ammo;
                    magazine.ammoType = ItemManager.FindItemDefinition(AmmoType);
                }

                if (flameThrower != null)
                {
                    flameThrower.ammo = FlameFuel;
                }

                if (DataInt > 0 || AssociatedEntityId != 0)
                {
                    item.instanceData = new ProtoBuf.Item.InstanceData
                    {
                        ShouldPool = false,
                        dataInt = DataInt,
                    };

                    if (AssociatedEntityId != 0)
                    {
                        var associatedEntity = BaseNetworkable.serverEntities.Find(AssociatedEntityId);
                        if (associatedEntity != null)
                        {
                            // Re-enable networking since it's disabled when the entity is disassociated.
                            associatedEntity._limitedNetworking = false;

                            item.instanceData.subEntity = AssociatedEntityId;
                        }
                    }
                }

                item.text = Text;

                return item;
            }
        }

        #endregion

        #region Stored Data

        [JsonObject(MemberSerialization.OptIn)]
        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(Backpacks));
                if (data == null)
                {
                    LogWarning($"Data file {nameof(Backpacks)}.json is invalid. Creating new data file.");
                    data = new StoredData { _dirty = true };
                    data.SaveIfChanged();
                }
                return data;
            }

            [JsonProperty("PlayersWithDisabledGUI")]
            private HashSet<ulong> DeprecatedPlayersWithDisabledGUI
            {
                set
                {
                    foreach (var playerId in value)
                    {
                        EnabledGuiPreference[playerId] = false;
                    }
                }
            }

            [JsonProperty("PlayerGuiPreferences")]
            private Dictionary<ulong, bool> EnabledGuiPreference = new Dictionary<ulong, bool>();

            [JsonIgnore]
            private bool _dirty;

            public bool? GetGuiButtonPreference(ulong userId)
            {
                bool guiEnabled;
                return EnabledGuiPreference.TryGetValue(userId, out guiEnabled)
                    ? guiEnabled as bool?
                    : null;
            }

            public bool ToggleGuiButtonPreference(ulong userId, bool defaultEnabled)
            {
                var enabledNow = !(GetGuiButtonPreference(userId) ?? defaultEnabled);
                EnabledGuiPreference[userId] = enabledNow;
                _dirty = true;
                return enabledNow;
            }

            public bool SaveIfChanged()
            {
                if (!_dirty)
                    return false;

                Interface.Oxide.DataFileSystem.WriteObject(nameof(Backpacks), this);
                _dirty = false;
                return true;
            }
        }

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Default Backpack Size")]
            public int DefaultBackpackSize = 6;

            [JsonProperty("Backpack Permission Sizes")]
            public int[] BackpackPermissionSizes = new int[] { 6, 12, 18, 24, 30, 36, 42, 48 };

            [JsonProperty("Enable Legacy Row Permissions (true/false)")]
            public bool EnableLegacyRowPermissions = true;

            [JsonProperty("Backpack Size (1-8 Rows)")]
            private int DeprecatedBackpackRows { set { DefaultBackpackSize = value * 6; } }

            [JsonProperty("Backpack Size (1-7 Rows)")]
            private int DeprecatedBackpackSize { set { DefaultBackpackSize = value * 6; } }

            [JsonProperty("Drop on Death (true/false)")]
            public bool DropOnDeath = true;

            [JsonProperty("Erase on Death (true/false)")]
            public bool EraseOnDeath = false;

            [JsonProperty("Clear Backpacks on Map-Wipe (true/false)")]
            public bool ClearBackpacksOnWipe = false;

            [JsonProperty("Use Blacklist (true/false)")]
            private bool UseDenylist = false;

            [JsonProperty("Blacklisted Items (Item Shortnames)")]
            private string[] DenylistItemShortNames = new string[]
            {
                "autoturret",
                "lmg.m249",
            };

            [JsonProperty("Use Whitelist (true/false)")]
            private bool UseAllowlist = false;

            [JsonProperty("Whitelisted Items (Item Shortnames)")]
            private string[] AllowedItemShortNames = new string[0];

            [JsonProperty("Minimum Despawn Time (Seconds)")]
            public float MinimumDespawnTime = 300;

            [JsonProperty("GUI Button")]
            public GUIButton GUI = new GUIButton();

            [JsonProperty("Softcore")]
            public SoftcoreOptions Softcore = new SoftcoreOptions();

            public class GUIButton
            {
                [JsonProperty("Enabled by default (for players with permission)")]
                public bool EnabledByDefault = true;

                [JsonProperty("Skin Id")]
                public ulong SkinId;

                [JsonProperty("Image")]
                public string Image = "https://i.imgur.com/CyF0QNV.png";

                [JsonProperty("Background Color")]
                public string Color = "0.969 0.922 0.882 0.035";

                [JsonProperty("GUI Button Position")]
                public Position GUIButtonPosition = new Position();

                public class Position
                {
                    [JsonProperty("Anchors Min")]
                    public string AnchorsMin = "0.5 0.0";

                    [JsonProperty("Anchors Max")]
                    public string AnchorsMax = "0.5 0.0";

                    [JsonProperty("Offsets Min")]
                    public string OffsetsMin = "185 18";

                    [JsonProperty("Offsets Max")]
                    public string OffsetsMax = "245 78";
                }
            }

            public class SoftcoreOptions
            {
                [JsonProperty("Reclaim Fraction")]
                public float ReclaimFraction = 0.5f;
            }

            [JsonIgnore]
            public bool ItemRestrictionEnabled => UseAllowlist || UseDenylist;

            [JsonIgnore]
            private readonly HashSet<int> _disallowedItemIds = new HashSet<int>();

            [JsonIgnore]
            private readonly HashSet<int> _allowedItemIds = new HashSet<int>();

            public void Init(Backpacks plugin)
            {
                if (UseDenylist)
                {
                    foreach (var itemShortName in DenylistItemShortNames)
                    {
                        var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                        if (itemDefinition != null)
                        {
                            _disallowedItemIds.Add(itemDefinition.itemid);
                        }
                        else
                        {
                            plugin.PrintWarning($"Invalid item short name in config: {itemShortName}");
                        }
                    }
                }
                else if (UseAllowlist)
                {
                    foreach (var itemShortName in AllowedItemShortNames)
                    {
                        var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                        if (itemDefinition != null)
                        {
                            _allowedItemIds.Add(itemDefinition.itemid);
                        }
                        else
                        {
                            plugin.PrintWarning($"Invalid item short name in config: {itemShortName}");
                        }
                    }
                }
            }

            public bool IsRestrictedItem(Item item)
            {
                return IsRestrictedItem(item.info.itemid);
            }

            public bool IsRestrictedItem(ItemData itemData)
            {
                return IsRestrictedItem(itemData.ID);
            }

            private bool IsRestrictedItem(int itemId)
            {
                if (UseAllowlist)
                    return !_allowedItemIds.Contains(itemId);

                if (UseDenylist)
                    return _disallowedItemIds.Contains(itemId);

                return false;
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            private string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigSection(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigSection(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigSection(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    PrintWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                PrintError(e.Message);
                PrintWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        #region Localization

        private string GetMessage(string playerId, string langKey) =>
            lang.GetMessage(langKey, this, playerId);

        private string GetMessage(IPlayer player, string langKey) =>
            GetMessage(player.Id, langKey);

        private string GetMessage(BasePlayer basePlayer, string langKey) =>
            GetMessage(basePlayer.UserIDString, langKey);

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
    }
}
