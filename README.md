**Backpacks** allows players to have backpacks that provide them with extra inventory space. The backpack can be dropped on death.

**Note:** To bind a key to open the backpack, use: `bind b backpack.open` in your F1 client console.

## Donations

Please consider donating to support me and help me put more time into my plugins. You can donate, by clicking [here](https://laserhydra.com/).

## Chat Commands

- `/backpack` -- open your own backpack
- `/viewbackpack <name or id>` -- open another players backpack **[Admin Command]**

## Permissions

- `backpacks.admin` -- required for `/viewbackpack` command
- `backpacks.use` -- required to open your own backpack
- `backpacks.use.1 - 7` -- gives player access to a certain amount of inventory rows overwriting the configured default size *(e.g. backpacks.use.3 gives them 3 rows of item space; still requires backpacks.use)*

## Configuration

```json
{
  "Drop on Death (true/false)": true,
  "Erase on Death (true/false)": false,
  "Use Blacklist (true/false)": false,
  "Clear Backpacks on Map-Wipe (true/false)": true,
  "Only Save Backpacks on Server-Save (true/false)": false,
  "Blacklisted Items (Item Shortnames)": [
    "autoturret",
    "lmg.m249"
  ],
  "Backpack Size (1-7 Rows)": 1
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
  "Multiple Players Found": "Multiple matching players found:\n{0}"
}
```

## API

```cs
void OnBackpackOpened(BasePlayer player, ItemContainer backpackContainer) // Triggered when player opens a backpack

void OnBackpackClosed(BasePlayer player, ItemContainer backpackContainer) // Triggered when the backpack is closed

string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID) // Returning string will cancel backpack opening and display this string as chat message
```
