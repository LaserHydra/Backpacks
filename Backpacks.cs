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

namespace Oxide.Plugins
{
    [Info("Backpacks", "LaserHydra", "3.8.0")]
    [Description("Allows players to have a Backpack which provides them extra inventory space.")]
    internal class Backpacks : RustPlugin
    {
        #region Fields

        private const ushort MinSize = 1;
        private const ushort MaxSize = 8;
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

        private const int SaddleBagItemId = 1400460850;

        private static readonly object True = true;
        private static readonly object False = false;

        private readonly BackpackManager _backpackManager;
        private Dictionary<string, ushort> _backpackSizePermissions = new Dictionary<string, ushort>();

        private ProtectionProperties _immortalProtection;
        private string _cachedUI;

        private ApiInstance _api;
        private Configuration _config;
        private StoredData _storedData;
        private int _wipeNumber;

        [PluginReference]
        private Plugin Arena, EventManager;

        public Backpacks()
        {
            _backpackManager = new BackpackManager(this);
            _api = new ApiInstance(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
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

            _storedData.Save();
            _backpackManager.SaveAndKillCachedBackpacks();
            _backpackManager.CleanupAllNetworkControllers();

            foreach (var player in BasePlayer.activePlayerList)
                DestroyGUI(player);
        }

        private void OnNewSave(string filename)
        {
            // Ensure config is loaded
            LoadConfig();

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

                var backpack = new Backpack(this, userId);
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
                _backpackManager.CleanupAllNetworkControllers();
                _backpackManager.ClearCache();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            var backpack = _backpackManager.GetCachedBackpack(player.userID);
            if (backpack == null)
                return;

            backpack.NetworkController?.Unsubscribe(player);

            if (!_config.SaveBackpacksOnServerSave)
            {
                backpack.SaveAndKill();
                _backpackManager.RemoveFromCache(backpack);
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

        private void OnNetworkSubscriptionsUpdate(Network.Networkable networkable, List<Network.Visibility.Group> groupsToAdd, List<Network.Visibility.Group> groupsToRemove)
        {
            if (groupsToRemove == null)
                return;

            for (var i = groupsToRemove.Count - 1; i >= 0; i--)
            {
                var group = groupsToRemove[i];
                if (_backpackManager.IsBackpackNetworkGroup(group))
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
            public Dictionary<string, object> _apiWrapper { get; private set; }

            private Backpacks _plugin;
            private BackpackManager _backpackManager => _plugin._backpackManager;

            public ApiInstance(Backpacks plugin)
            {
                _plugin = plugin;

                _apiWrapper = new Dictionary<string, object>
                {
                    [nameof(GetExistingBackpacks)] = new Func<Dictionary<ulong, ItemContainer>>(GetExistingBackpacks),
                    [nameof(EraseBackpack)] = new Action<ulong>(EraseBackpack),
                    [nameof(DropBackpack)] = new Func<BasePlayer, DroppedItemContainer>(DropBackpack),
                    [nameof(GetBackpackOwnerId)] = new Func<ItemContainer, ulong>(GetBackpackOwnerId),
                    [nameof(GetBackpackContainer)] = new Func<ulong, ItemContainer>(GetBackpackContainer),
                    [nameof(GetBackpackItemAmount)] = new Func<ulong, int, ulong, int>(GetBackpackItemAmount),
                    [nameof(OpenBackpack)] = new Func<BasePlayer, ulong, bool>(OpenBackpack),
                };
            }

            public Dictionary<ulong, ItemContainer> GetExistingBackpacks()
            {
                return _backpackManager.GetAllCachedContainers();
            }

            public void EraseBackpack(ulong userId)
            {
                _backpackManager.TryEraseForPlayer(userId);
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
                return _backpackManager.GetBackpackIfExists(ownerId)?.GetItemQuantity(itemId, skinId) ?? 0;
            }

            public bool OpenBackpack(BasePlayer player, ulong ownerId)
            {
                if (ownerId == 0)
                {
                    ownerId = player.userID;
                }

                return _backpackManager.OpenBackpack(ownerId, player);
            }
        }

        [HookMethod(nameof(API_GetApi))]
        public Dictionary<string, object> API_GetApi()
        {
            return _api._apiWrapper;
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
        public ulong API_GetBackpackOwnerId(ItemContainer container)
        {
            return _api.GetBackpackOwnerId(container);
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

        [HookMethod(nameof(API_OpenBackpack))]
        public object API_OpenBackpack(BasePlayer player, ulong ownerId = 0)
        {
            return BooleanNoAlloc(_api.OpenBackpack(player, ownerId));
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object CanOpenBackpack(BasePlayer looter, ulong ownerId)
            {
                return Interface.CallHook("CanOpenBackpack", looter, ownerId);
            }

            public static void OnBackpackClosed(BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackClosed", looter, ownerId, container);
            }

            public static void OnBackpackOpened(BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackOpened", looter, ownerId, container);
            }

            public static object CanDropBackpack(ulong ownerId, Vector3 position)
            {
                return Interface.CallHook("CanDropBackpack", ownerId, position);
            }

            public static object CanEraseBackpack(ulong ownerId)
            {
                return Interface.CallHook("CanEraseBackpack", ownerId);
            }

            public static object CanBackpackAcceptItem(ulong ownerId, ItemContainer container, Item item)
            {
                return Interface.CallHook("CanBackpackAcceptItem", ownerId, container, item);
            }
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
                : IsKeyBindArg(arg.Args?.LastOrDefault())
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

        private static bool IsKeyBindArg(string arg)
        {
            return arg == "True";
        }

        private static object BooleanNoAlloc(bool value)
        {
            return value ? True : False;
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

            var hookResult = ExposedHooks.CanOpenBackpack(looter, ownerId);
            if (hookResult != null && hookResult is string)
            {
                PrintToChat(looter, hookResult as string);
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

                cuiElements.Add(new CuiElement
                {
                    Parent = GUIPanelName,
                    Components = {
                        _config.GUI.SkinId != 0
                            ? new CuiImageComponent { ItemId = SaddleBagItemId, SkinId = _config.GUI.SkinId }
                            : new CuiRawImageComponent { Url = _config.GUI.Image } as ICuiComponent,
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

        private class Configuration : BaseConfiguration
        {
            private ushort _backpackSize = 1;

            [JsonProperty("Backpack Size (1-8 Rows)")]
            public ushort BackpackSize
            {
                get { return _backpackSize; }
                set { _backpackSize = (ushort) Mathf.Clamp(value, MinSize, MaxSize); }
            }

            [JsonProperty("Backpack Size (1-7 Rows)")]
            public ushort BackpackSizeDeprecated
            {
                set
                {
                    BackpackSize = value;
                }
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

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                BlacklistedItems = new HashSet<string>
                {
                    "autoturret",
                    "lmg.m249",
                },
            };
        }

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            [JsonIgnore]
            public bool UsingDefaults = false;

            public string ToJson() => JsonConvert.SerializeObject(this);

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

        #region Stored Data

        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(Backpacks));
                if (data == null)
                {
                    Interface.Oxide.LogWarning($"Data file {nameof(Backpacks)}.json is invalid. Creating new data file.");
                    data = new StoredData();
                    data.Save();
                }
                return data;
            }

            [JsonProperty("PlayersWithDisabledGUI")]
            public HashSet<ulong> PlayersWithDisabledGUI = new HashSet<ulong>();

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(nameof(Backpacks), this);
        }

        #endregion

        #region Backpack Manager

        private class BackpackManager
        {
            private const uint StartNetworkGroupId = 10000000;

            private Backpacks _plugin;
            private uint _nextNetworkGroupId = StartNetworkGroupId;

            private readonly Dictionary<ulong, Backpack> _cachedBackpacks = new Dictionary<ulong, Backpack>();
            private readonly Dictionary<ItemContainer, Backpack> _backpackContainers = new Dictionary<ItemContainer, Backpack>();

            public BackpackManager(Backpacks plugin)
            {
                _plugin = plugin;
            }

            public bool IsBackpackNetworkGroup(Network.Visibility.Group group)
            {
                return group.ID >= StartNetworkGroupId && group.ID < _nextNetworkGroupId;
            }

            public BackpackNetworkController CreateNetworkController()
            {
                return new BackpackNetworkController(_nextNetworkGroupId++);
            }

            private static string GetBackpackPath(ulong userId) => $"{nameof(Backpacks)}/{userId}";

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
                return _cachedBackpacks.TryGetValue(userId, out backpack)
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

                foreach (var entry in _cachedBackpacks)
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

            public bool OpenBackpack(ulong backpackOwnerId, BasePlayer looter)
            {
                return GetBackpack(backpackOwnerId).Open(looter);
            }

            private Backpack Load(ulong userId)
            {
                var fileName = GetBackpackPath(userId);

                TryMigrateData(fileName);

                Backpack backpack = null;
                if (HasBackpackFile(userId))
                {
                    LoadData(out backpack, fileName);
                    backpack.Plugin = _plugin;
                }

                // Note: Even if the user has a backpack file, the file contents may be null in some edge cases.
                if (backpack == null)
                {
                    backpack = new Backpack(_plugin, userId);
                }

                // Ensure the backpack always has an owner id.
                // This improves compatibility with plugins such as Wipe Data Cleaner which reset the file to `{}`.
                backpack.OwnerId = userId;

                // Forget about associated entities from previous wipes.
                if (backpack.WipeNumber != _plugin._wipeNumber)
                {
                    foreach (var itemData in backpack.ItemDataCollection)
                    {
                        itemData.AssociatedEntityId = 0;
                    }
                }

                backpack.WipeNumber = _plugin._wipeNumber;

                _cachedBackpacks[userId] = backpack;

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
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    backpack.SaveAndKill();
                }
            }

            public void RemoveFromCache(Backpack backpack)
            {
                _cachedBackpacks.Remove(backpack.OwnerId);
            }

            public void ClearCache()
            {
                _cachedBackpacks.Clear();
            }

            public void CleanupAllNetworkControllers()
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    backpack.NetworkController?.UnsubscribeAll();
                }
                _nextNetworkGroupId = StartNetworkGroupId;
            }
        }

        #endregion

        #region Backpack Networking

        private class BackpackNetworkController
        {
            public Network.Visibility.Group NetworkGroup { get; private set; }

            private List<BasePlayer> _subscribers = new List<BasePlayer>(1);

            public BackpackNetworkController(uint networkGroupId)
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
                ExposedHooks.OnBackpackClosed(looter, _backpack.OwnerId, _backpack.GetContainer());

                if (_plugin.IsLoaded && !_plugin._config.SaveBackpacksOnServerSave)
                {
                    _backpack.SaveData();
                }

                _plugin.TrackEnd();
            }
        }

        private class Backpack
        {
            private StorageContainer _storageContainer;
            private ItemContainer _itemContainer;
            private List<BasePlayer> _looters = new List<BasePlayer>();
            private bool _processedRestrictedItems;
            private bool _dirty = false;

            [JsonIgnore]
            public Backpacks Plugin;

            [JsonIgnore]
            public BackpackNetworkController NetworkController { get; private set; }

            [JsonIgnore]
            public string OwnerIdString { get; private set; }

            [JsonProperty("OwnerID")]
            public ulong OwnerId { get; set; }

            [JsonProperty("WipeNumber", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int WipeNumber;

            [JsonProperty("Items")]
            public List<ItemData> ItemDataCollection = new List<ItemData>();

            public Backpack(Backpacks plugin, ulong ownerId) : base()
            {
                Plugin = plugin;
                OwnerId = ownerId;
                OwnerIdString = OwnerId.ToString();
            }

            ~Backpack()
            {
                KillContainer();
                NetworkController?.UnsubscribeAll();
            }

            public IPlayer FindOwnerPlayer() => Plugin.covalence.Players.FindPlayerById(OwnerIdString);

            public ushort GetAllowedSize()
            {
                foreach(var kvp in Plugin._backpackSizePermissions)
                {
                    if (Plugin.permission.UserHasPermission(OwnerIdString, kvp.Key))
                        return kvp.Value;
                }

                return Plugin._config.BackpackSize;
            }

            public ItemContainer GetContainer(bool ensureContainer = false)
            {
                if (ensureContainer)
                    EnsureContainer();

                return _itemContainer;
            }

            public bool Open(BasePlayer looter)
            {
                if (!Plugin.VerifyCanOpenBackpack(looter, OwnerId))
                    return false;

                EnsureContainer();
                NetworkController.Subscribe(looter);

                // Only drop items when the owner is opening the backpack.
                if (looter.userID == OwnerId)
                {
                    MaybeRemoveRestrictedItems(looter);
                    MaybeAdjustCapacityAndHandleOverflow(looter);
                }

                if (!_looters.Contains(looter))
                    _looters.Add(looter);

                _storageContainer.PlayerOpenLoot(looter, _storageContainer.panelName, doPositionChecks: false);

                ExposedHooks.OnBackpackOpened(looter, OwnerId, _storageContainer.inventory);
                return true;
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

            public DroppedItemContainer Drop(Vector3 position)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                if (_storageContainer == null && ItemDataCollection.Count == 0)
                    return null;

                object hookResult = ExposedHooks.CanDropBackpack(OwnerId, position);

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
                container.ResetRemovalTime(Math.Max(Plugin._config.MinimumDespawnTime, container.CalculateRemovalTime()));

                if (!Plugin._config.SaveBackpacksOnServerSave)
                {
                    SaveData();
                }

                return container;
            }

            public void EraseContents(bool force = false)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                if (_storageContainer == null && ItemDataCollection.Count == 0)
                    return;

                if (!force)
                {
                    object hookResult = ExposedHooks.CanEraseBackpack(OwnerId);

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
                    ItemDataCollection.Clear();
                }

                if (!Plugin._config.SaveBackpacksOnServerSave)
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
                    ItemDataCollection = _itemContainer.itemList
                        .Select(ItemData.FromItem)
                        .ToList();
                }

                Backpacks.SaveData(this, $"{nameof(Backpacks)}/{OwnerId}");
                _dirty = false;
            }

            public void SaveAndKill()
            {
                SaveData();
                KillContainer();
            }

            public int GetItemQuantity(int itemID, ulong skinId = 0)
            {
                if (_itemContainer != null)
                    return _itemContainer.GetAmount(itemID, onlyUsableAmounts: false);

                var count = 0;
                foreach (var itemData in ItemDataCollection)
                {
                    if (itemData.ID != itemID)
                        continue;

                    if (skinId != 0 && itemData.Skin != skinId)
                        continue;

                    count += itemData.Amount;
                }
                return count;
            }

            public int MoveItemsToPlayerInventory(BasePlayer player, int itemID, int desiredAmount)
            {
                EnsureContainer();

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

                if (amountTransferred > 0 && !Plugin._config.SaveBackpacksOnServerSave)
                    SaveData();

                return amountTransferred;
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

            private void KillContainer()
            {
                ForceCloseAllLooters();

                if (_itemContainer != null)
                {
                    foreach (var item in _itemContainer.itemList)
                    {
                        DisassociateEntity(item);
                    }
                }

                if (_storageContainer != null && !_storageContainer.IsDestroyed)
                {
                    Plugin._backpackManager.UnregisterContainer(_itemContainer);
                    _storageContainer.Kill();
                }
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
                if (_looters.Count == 0)
                    return;

                foreach (BasePlayer looter in _looters.ToArray())
                {
                    ForceCloseLooter(looter);
                }
            }

            private int GetAllowedCapacity() => GetAllowedSize() * SlotsPerRow;

            private int GetHighestUsedSlot()
            {
                var highestUsedSlot = -1;
                foreach (var itemData in ItemDataCollection)
                {
                    if (itemData.Position > highestUsedSlot)
                        highestUsedSlot = itemData.Position;
                }
                return highestUsedSlot;
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

            private bool ShouldAcceptItem(Item item)
            {
                // Skip checking restricted items if they haven't been processed, to avoid erasing them.
                // Restricted items will be dropped when the owner opens the backpack.
                if (Plugin._config.ItemRestrictionEnabled
                    && _processedRestrictedItems
                    && !Plugin.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission)
                    && Plugin._config.IsRestrictedItem(item))
                {
                    return false;
                }

                object hookResult = ExposedHooks.CanBackpackAcceptItem(OwnerId, _itemContainer, item);
                if (hookResult is bool && (bool)hookResult == false)
                    return false;

                return true;
            }

            private bool CanAcceptItem(Item item, int amount)
            {
                // Explicitly track hook time so server owners can be informed of the cost.
                Plugin.TrackStart();
                var result = ShouldAcceptItem(item);
                Plugin.TrackEnd();
                return result;
            }

            private void EnsureContainer()
            {
                if (_storageContainer != null)
                    return;

                if (NetworkController == null)
                    NetworkController = Plugin._backpackManager.CreateNetworkController();

                _storageContainer = SpawnStorageContainer(GetAllowedCapacity());
                _itemContainer = _storageContainer.inventory;

                Plugin._backpackManager.RegisterContainer(_itemContainer, this);

                if (GetHighestUsedSlot() >= _itemContainer.capacity)
                {
                    // Temporarily increase the capacity to allow all items to fit
                    // Extra items will be addressed when the backpack is opened by the owner
                    // If an admin views the backpack in the meantime, it will appear as max capacity
                    _itemContainer.capacity = MaxSize * SlotsPerRow;
                }

                foreach (var backpackItem in ItemDataCollection)
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
                _itemContainer.onDirty += () => _dirty = true;
            }

            private void MaybeRemoveRestrictedItems(BasePlayer receiver)
            {
                if (!Plugin._config.ItemRestrictionEnabled)
                    return;

                // Optimization: Avoid processing item restrictions every time the backpack is opened.
                if (_processedRestrictedItems)
                    return;

                if (Plugin.permission.UserHasPermission(OwnerIdString, NoBlacklistPermission))
                {
                    // Don't process item restrictions while the player has the noblacklist permission.
                    // Setting this flag allows the item restrictions to be processed again in case the noblacklist permission is revoked.
                    _processedRestrictedItems = false;
                    return;
                }

                var itemsDroppedOrGivenToPlayer = 0;
                for (var i = _itemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = _itemContainer.itemList[i];
                    if (Plugin._config.IsRestrictedItem(item))
                    {
                        itemsDroppedOrGivenToPlayer++;
                        item.RemoveFromContainer();
                        receiver.GiveItem(item);
                    }
                }

                if (itemsDroppedOrGivenToPlayer > 0)
                {
                    Plugin.PrintToChat(receiver, Plugin.lang.GetMessage("Blacklisted Items Removed", Plugin, receiver.UserIDString));
                }

                _processedRestrictedItems = true;
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
                    Plugin.PrintToChat(receiver, Plugin.lang.GetMessage("Backpack Over Capacity", Plugin, receiver.UserIDString));
                }
            }

            private void ReclaimItemsForSoftcore()
            {
                var softcoreGameMode = BaseGameMode.svActiveGameMode as GameModeSoftcore;
                if (softcoreGameMode == null || ReclaimManager.instance == null)
                    return;

                List<Item> reclaimItemList = Facepunch.Pool.GetList<Item>();
                softcoreGameMode.AddFractionOfContainer(_itemContainer, ref reclaimItemList, Plugin._config.Softcore.ReclaimFraction);
                if (reclaimItemList.Count > 0)
                {
                    // There's a vanilla bug where accessing the reclaim backpack will erase items in the reclaim entry above 32.
                    // So we just add a new reclaim entry which can only be accessed at the terminal to avoid this issue.
                    ReclaimManager.instance.AddPlayerReclaim(OwnerId, reclaimItemList);
                }
                Facepunch.Pool.FreeList(ref reclaimItemList);
            }
        }

        public class ItemData
        {
            [JsonProperty("ID")]
            public int ID;

            [JsonProperty("Position")]
            public int Position = -1;

            [JsonProperty("Amount")]
            public int Amount;

            [JsonProperty("IsBlueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool IsBlueprint;

            [JsonProperty("BlueprintTarget", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int BlueprintTarget;

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin;

            [JsonProperty("Fuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Fuel;

            [JsonProperty("FlameFuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int FlameFuel;

            [JsonProperty("Condition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Condition;

            [JsonProperty("MaxCondition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float MaxCondition = -1;

            [JsonProperty("Ammo", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Ammo;

            [JsonProperty("AmmoType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int AmmoType;

            [JsonProperty("DataInt", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt;

            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

            [JsonProperty("Text", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Text;

            [JsonProperty("Flags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Item.Flag Flags;

            [JsonProperty("AssociatedEntityId", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public uint AssociatedEntityId;

            [JsonProperty("Contents", DefaultValueHandling = DefaultValueHandling.Ignore)]
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
                {
                    item.maxCondition = MaxCondition;
                }

                if (Name != null)
                {
                    item.name = Name;
                }

                if (Contents?.Count > 0)
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
                AssociatedEntityId = item.instanceData?.subEntity ?? 0,
                Name = item.name,
                Text = item.text,
                Flags = item.flags,
            };
        }

        #endregion
    }
}