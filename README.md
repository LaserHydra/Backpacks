﻿**Backpacks** allows players to have backpacks that provide them with extra inventory space. The backpack can be dropped on death. Supports GUI button.

**Note:** To bind a key to open the backpack, use: `bind b backpack.open` in your F1 client console.

## Donations

Please consider donating to support me and help me put more time into my plugins. You can donate, by clicking [here](https://laserhydra.com/).

## Chat Commands

- `/backpack` -- open your own backpack
- `/viewbackpack <name or id>` -- open another players backpack **[Admin Command]**

## Console Commands

- `backpack.fetch <item short name or id> <amount>` -- Fetch an item from your backpack

## Permissions

- `backpacks.admin` -- required for `/viewbackpack` command
- `backpacks.gui` -- required to use GUI button
- `backpacks.use` -- required to open your own backpack
- `backpacks.use.1 - 7` -- gives player access to a certain amount of inventory rows overwriting the configured default size *(e.g. backpacks.use.3 gives them 3 rows of item space; still requires backpacks.use)*
- `backpacks.fetch` -- required to use the `backpack.fetch` command
- `backpacks.keepondeath` -- exempts player from having their backpack erased or dropped on death

## Configuration

```json
{
  "Drop on Death (true/false)": true,
  "Erase on Death (true/false)": false,
  "Use Blacklist (true/false)": false,
  "Clear Backpacks on Map-Wipe (true/false)": false,
  "Only Save Backpacks on Server-Save (true/false)": false,
  "Blacklisted Items (Item Shortnames)": [
    "autoturret",
    "lmg.m249"
  ],
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
  "Backpack Size (1-7 Rows)": 1
}
```

Alternative backpacks buttons:
```json
//button left side
  "GUI Button": {
    "Image": "https://i.imgur.com/wLR9Z6V.png",
    "Background color (RGBA format)": "1 0.96 0.88 0.15",
    "GUI Button Position": {
      "Anchors Min": "0.5 0.0",
      "Anchors Max": "0.5 0.0",
      "Offsets Min": "-265 18",
      "Offsets Max": "-205 78"
    }
  },
```
```json
//button right side
  "GUI Button": {
    "Image": "https://i.imgur.com/h1HQEAB.png",
    "Background color (RGBA format)": "1 0.96 0.88 0.15",
    "GUI Button Position": {
      "Anchors Min": "0.5 0.0",
      "Anchors Max": "0.5 0.0",
      "Offsets Min": "185 18",
      "Offsets Max": "245 78"
    }
  },
```
```json
//Universal image
    "Image": "https://i.imgur.com/5RE9II5.png",
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
  "Backpack Fetch Syntax": "Syntax: backpack.fetch <item short name or id> <amount>",
  "Invalid Item": "Invalid Item Name or ID.",
  "Invalid Item Amount": "Item amount must be an integer greater than 0.",
  "Item Not In Backpack": "Item \"{0}\" not found in backpack.",
  "Items Fetched": "Fetched {0} \"{1}\" from backpack.",
  "Fetch Failed": "Couldn't fetch \"{0}\" from backpack. Inventory may be full."
}
```

## Hooks

### CanOpenBackpack

Called when a player tries to open a backpack.  
Returning a string will cancel backpack opening and send the string as a chat message to the player trying to open the backpack.  

```csharp
string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
```
  
### OnBackpackOpened

Called when a player successfully opened a backpack.  
No return behaviour.  

```csharp
void OnBackpackOpened(BasePlayer player, ulong backpackOwnerID, ItemContainer backpackContainer)
```
  
### OnBackpackClosed

Called when a player closed a backpack.  
No return behaviour.  

```csharp
void OnBackpackClosed(BasePlayer player, ulong backpackOwnerID, ItemContainer backpackContainer)
```

### CanBackpackAcceptItem

Called when a player tries to move an item into a backpack.  
Returning false prevents the item being moved.    

```csharp
bool CanBackpackAcceptItem(ulong backpackOwnerID, ItemContainer backpackContainer, Item item)
```

### CanDropBackpack

Called when a player dies and the "Drop on Death" option is set to true.  
Returning false prevents the backpack dropping.  

```csharp
bool CanDropBackpack(ulong backpackOwnerID, Vector3 position)
```

### CanEraseBackpack

Called when a player dies and the "Erase on Death" option is set to true.  
Returning false prevents the backpack being erased.  

```csharp
bool CanEraseBackpack(ulong backpackOwnerID)
```