## Features

Allows players to have backpacks that provide them with extra inventory space.

- Customizable capacity per player
- Option to drop or erase contents on death
- Option to clear on map wipe
- Optional item whitelist or blacklist
- Optional GUI button

**Note:** To bind a key to open the backpack, use: `bind b backpack.open` in your F1 client console.

## Donations

Please consider donating to support me and help me put more time into my plugins. You can donate, by clicking [here](https://laserhydra.com/).

## Chat Commands

- `/backpack` -- open your own backpack
- `/backpackgui` -- toggle whether you can see the backpack GUI button
- `/viewbackpack <name or id>` -- open another player's backpack (requires `backpacks.admin` permission)

## Console Commands

- `backpack.open` -- open your backpack (for key bind, also invoked by the GUI button)
- `backpack.fetch <item short name or id> <amount>` -- fetch an item from your backpack

## Server Commands

- `backpack.erase <id>` -- forcibly erase the contents of a specific player's backpack

## Permissions

- `backpacks.admin` -- required to use the `/viewbackpack` command
- `backpacks.gui` -- required to see GUI button
- `backpacks.use` -- required to open your own backpack
- `backpacks.use.1 - 7` -- gives player access to a certain amount of inventory rows, overriding the configured default size *(e.g. backpacks.use.3 gives them 3 rows of item space; still requires backpacks.use)*
- `backpacks.fetch` -- required to use the `backpack.fetch` command
- `backpacks.keepondeath` -- exempts player from having their backpack erased or dropped on death
- `backpacks.keeponwipe` -- exempts player from having their backpack erased on map wipe
- `backpacks.noblacklist` -- exempts player from item restrictions (blacklist or whitelist)

## Configuration

```json
{
  "Drop on Death (true/false)": true,
  "Erase on Death (true/false)": false,
  "Clear Backpacks on Map-Wipe (true/false)": false,
  "Only Save Backpacks on Server-Save (true/false)": false,
  "Use Blacklist (true/false)": false,
  "Blacklisted Items (Item Shortnames)": [
    "autoturret",
    "lmg.m249"
  ],
  "Use Whitelist (true/false)": false,
  "Whitelisted Items (Item Shortnames)": [],
  "Minimum Despawn Time (Seconds)": 300.0,
  "GUI Button": {
    "Image": "https://i.imgur.com/CyF0QNV.png",
    "Background color (RGBA format)": "1 0.96 0.88 0.15",
    "GUI Button Position": {
      "Anchors Min": "0.5 0.0",
      "Anchors Max": "0.5 0.0",
      "Offsets Min": "185 18",
      "Offsets Max": "245 78"
    }
  },
  "Softcore": {
    "Reclaim Fraction": 0.5
  },
  "Backpack Size (1-7 Rows)": 1
}
```

Note: When using the item whitelist, the blacklist is ignored.

#### Backpack icon customization

Alternative backpacks images:

- [Right-side](https://i.imgur.com/h1HQEAB.png)
- [Left-side](https://i.imgur.com/wLR9Z6V.png)
- [Universal](https://i.imgur.com/5RE9II5.png)

Left-side button coordinates:

```json
    "GUI Button Position": {
      "Anchors Min": "0.5 0.0",
      "Anchors Max": "0.5 0.0",
      "Offsets Min": "-265 18",
      "Offsets Max": "-205 78"
    }
```

## Localization

```json
{
  "No Permission": "You don't have permission to use this command.",
  "May Not Open Backpack In Event": "You may not open a backpack while participating in an event!",
  "View Backpack Syntax": "Syntax: /viewbackpack <name or id>",
  "User ID not Found": "Could not find player with ID '{0}'",
  "User Name not Found": "Could not find player with name '{0}'",
  "Multiple Players Found": "Multiple matching players found:\n{0}",
  "Backpack Over Capacity": "Your backpack was over capacity. Overflowing items were added to your inventory or dropped.",
  "Blacklisted Items Removed": "Your backpack contained blacklisted items. They have been added to your inventory or dropped.",
  "Backpack Fetch Syntax": "Syntax: backpack.fetch <item short name or id> <amount>",
  "Invalid Item": "Invalid Item Name or ID.",
  "Invalid Item Amount": "Item amount must be an integer greater than 0.",
  "Item Not In Backpack": "Item \"{0}\" not found in backpack.",
  "Items Fetched": "Fetched {0} \"{1}\" from backpack.",
  "Fetch Failed": "Couldn't fetch \"{0}\" from backpack. Inventory may be full.",
  "Toggled Backpack GUI": "Toggled backpack GUI button."
}
```

## Known limitations

Paintable entities, photos, pagers, mobile phones, and cassettes will lose their data on map wipe. There are currently no plans to persist this data across wipes, but such a feature can be considered upon request.

## Developer API

### API_GetBackpackContainer

```csharp
ItemContainer API_GetBackpackContainer(ulong backpackOwnerID)
```

Returns a reference to the underlying `ItemContainer` of a player's backpack. Returns `null` if the player essentially has no backpack (no data file and no backpack in-memory).

Notes:
- This will create the container entity if it doesn't exist. This can add load to the server, so it's recommended to use this API only if the other API methods do not meet your needs. For example, if you want to know only the quantity of an item in the player's backpack, you can use `API_GetBackpackItemAmount` which can count the items without creating the container.
- You should avoid caching the container because several events may cause the backpack's underlying container to be replaced or deleted, which would make the cached reference useless.

### API_GetBackpackItemAmount

```csharp
int API_GetBackpackItemAmount(ulong backpackOwnerID, int itemId)
```

Returns the quantity of a given item in the player's backpack. Returns `0` if the player has no backpack. This API is more performant than `API_GetBackpackContainer` because it does not require creating the backpack container.

### API_DropBackpack

```csharp
DroppedItemContainer API_DropBackpack(BasePlayer player)
```

Drop the player's backpack at their current position. This can be used, for example, to only drop the player's backpack when they die in a PvP zone.

Note: This intentionally ignores the player's `backpacks.keepondeath` permission in order to provide maximum flexibility to other plugins, so it's recommended that other plugins provide a similar permission to allow exemptions.

### API_EraseBackpack

```csharp
void API_EraseBackpack(ulong backpackOwnerID)
```

Erases the contents of a specific player's backpack.

Note: This cannot be blocked by the `CanEraseBackpack` hook.

### API_GetBackpackOwnerId

```csharp
ulong API_GetBackpackOwnerId(ItemContainer container)
```

- Returns the Steam ID of the backpack owner if the `ItemContainer` is a backpack.
- Returns `0` if the `ItemContainer` is **not** a backpack.

### API_GetExistingBackpacks

```csharp
Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
```

Returns all backpack containers that are cached in the plugin's memory, keyed by the Steam IDs of the backpack owners. This was originally contributed so that item cleaner plugins could determine which items were in backpacks in order to ignore them. However, as of Backpacks v3.7.0, all item cleaner plugins should automatically be compatible if they verify that the container has a valid `entityOwner`.

## Developer Hooks

### CanOpenBackpack

```csharp
string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
```

Called when a player tries to open a backpack.
Returning a string will cancel backpack opening and send the string as a chat message to the player trying to open the backpack.

### OnBackpackOpened

```csharp
void OnBackpackOpened(BasePlayer player, ulong backpackOwnerID, ItemContainer backpackContainer)
```

- Called when a player successfully opens a backpack.
- No return behaviour.

### OnBackpackClosed

```csharp
void OnBackpackClosed(BasePlayer player, ulong backpackOwnerID, ItemContainer backpackContainer)
```

- Called when a player closes a backpack.
- No return behaviour.

### CanBackpackAcceptItem

```csharp
bool CanBackpackAcceptItem(ulong backpackOwnerID, ItemContainer backpackContainer, Item item)
```

- Called when a player tries to move an item into a backpack.
- Returning `false` prevents the item being moved.

### CanDropBackpack

```csharp
bool CanDropBackpack(ulong backpackOwnerID, Vector3 position)
```

- Called when a player dies while the `"Drop on Death (true/false)"` option is set to `true`.
- Returning `false` prevents the backpack from dropping.

### CanEraseBackpack

```csharp
bool CanEraseBackpack(ulong backpackOwnerID)
```

- Called when a player dies while the `"Erase on Death (true/false)"` option is set to `true`.
- Returning `false` prevents the backpack from being erased.
