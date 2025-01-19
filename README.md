## Features

Allows players to have backpacks that provide them with extra inventory space.

- Customizable capacity per player (using permissions)
- Option to drop or erase backpack contents on death
- Option to clear backpack contents on map wipe
- Optional item restrictions
- Optional GUI button to access the backpack
- Optionally auto gather newly acquired items into your backpack
- Optionally auto retrieve items from your backpack for crafting, building, etc.

**Note:** To bind a key to open the backpack, use: `bind <key> backpack` in your F1 client console. For example, `bind b backpack`.

## Quick start

### Allow players to open backpacks

To allow **all players** to use backpacks, run the following command.

```
o.grant group default backpacks.use
```

That will allow all players to open their backpack with the `/backpack` chat command or the `backpack` console command. By default, all backpacks have 6 slots of capacity (1 row), but that can be changed in the configuration and by assigning permissions.

### Allow players to use the GUI button

To allow **all players** to view the backpack GUI button, run the following command.

```
o.grant group default backpacks.gui
```

Players can click that button (next to their belt) to open and close their backpack. They can also run the `/backpackgui` chat command to hide or show the button. If you want to disable the button by default, so that players have to enable it explicitly, you may do so in the configuration.

Alternatively, instead of enabling the GUI button in this plugin, consider using the [Backpack Button](https://umod.org/plugins/backpack-button) plugin for more advanced functionality.

## Commands

- `backpack` / `backpack.open` -- Opens your own backpack. Requires the `backpacks.use` permission. If the backpack is already open, this will advance to the next page, or will close the player inventory if there are no more pages.
- `backpack.fetch <item short name or id> <amount>` -- Fetches an item from your backpack.
- `backpackgui` -- Toggles whether you can see the backpack GUI button.
- `backpack.setgathermode <All|Existing|Off> <optional page number>` -- Sets gather mode for the specified backpack page. If the page number is not specified, this applies to the first page. This command is useful for quickly changing gather mode via a key bind without opening your backpack. Note: If you want to consistently receive feedback for this command via chat instead of via console, you should bind it like `bind <key> chat.say "/backpack.setgathermode All"` instead of `bind <key> backpack.setgathermode All`.

## Admin commands

- `viewbackpack <name or steam id>` -- Opens another player's backpack (requires `backpacks.admin` permission)
- `backpack.addsize <name or steam id> <amount>` -- Attempts to change the size of the specified player's backpack by the specified amount, without going outside the bounds of the player's size profile. This can be used to decrease capacity by providing a negative number. Requires the experimental dynamic size feature to be enabled in the config.
- `backpack.setsize <name or steam id> <amount>` -- Attempts to set the size of the player's backpack to the specified amount, without going outside the bounds of the player's size profile. Requires the experimental dynamic size feature to be enabled in the config.

## Server commands

- `backpack.erase <steam id>` -- Forcibly erases **all** the contents of a specific player's backpack, even if they have `backpack.keeponwipe.*` permissions that would normally exempt them.

## Permissions

- `backpacks.use` -- Required to open your own backpack.
- `backpacks.admin` -- Required to use the `viewbackpack`, `backpack.addsize` and `backpack.setsize` commands.
- `backpacks.admin.protected` -- Protects your backpack from being viewed by other administrators via the `viewbackpack` command.
- `backpacks.gui` -- Required to see the GUI button.
- `backpacks.fetch` -- Required to use the `backpack.fetch` command.
- `backpacks.keepondeath` -- Exempts you from having your backpack erased or dropped on death.
- `backpacks.gather` -- Allows you to enable gather mode per backpack page, which automatically transfers newly acquired inventory items to your backpack.
  - **Note**: When you disconnect from the server and reconnect some time later, gather mode will not be activated until you open your backpack at least once.
- `backpacks.retrieve` -- Allows you to enable retrieve mode per backpack page. When retrieve mode is enabled, you can build, craft and more using items from your designated backpack pages. Requires the [Item Retriever](https://umod.org/plugins/item-retriever) plugin.
  - **Note**: When you disconnect from the server and reconnect some time later, retrieve mode will not be activated until you open your backpack at least once. Additionally, reloading weapon ammo, switching weapon ammo, purchasing items from vending machines, and purchasing vehicles from NPC vendors will not be able to pull items from pages until you access those specific pages at least once after (re)connecting to the server.

### Size permissions

If you want to grant specific players or groups more backpack capacity than the default (`Backpack size` -> `Default size`), then you may do so via permissions. Each number present in the `Backpack size` -> `Permission sizes` config option will cause the plugin to generate a permission of the format `backpacks.size.<number>`, which assigns the corresponding players or groups that much capacity. For example, `backpacks.size.18` would assign 18 slots of capacity (3 rows).

The following permissions come with the plugin's **default configuration**.
 
- `backpacks.size.6` -- 1 rows
- `backpacks.size.12` -- 2 rows
- `backpacks.size.18` -- 3 rows
- `backpacks.size.24` -- 4 rows
- `backpacks.size.30` -- 5 rows
- `backpacks.size.36` -- 6 rows
- `backpacks.size.42` -- 7 rows
- `backpacks.size.48` -- 8 rows
- `backpacks.size.96` -- 16 rows (2+ pages)
- `backpacks.size.144` -- 24 rows (3+ pages)

Additional permissions may be defined by simply adding them to the `Backpack size` -> `Permission sizes` config option and reloading the plugin.

**Note:** If a player is granted multiple size permissions, the highest will apply.

### Dynamic size (ADVANCED / EXPERIMENTAL)

If you want to allow players to upgrade the size of their backpack, it is possible to do so via the `backpacks.size.*` permissions above, by simply granting higher permissions as the player makes purchases or performs actions to earn the bigger size (you are responsible for using other plugins to grant and/or revoke permissions at the appropriate time). However, using permissions to determine backpack size is not the best fit for all types of servers, resulting in some example problems below.

- If you want backpack size to be reset at the beginning of each wipe, you would need another plugin to reset permissions at that time.
- If you want to allow players to increase their backpack size multiple ways, such as via a skill tree, via store purchases, via consumable items found and/or traded amongst players, that combination simply wouldn't be feasible because each upgrade source could override the others or have no effect if the permission granted corresponded to a smaller size than the player currently has.

Dynamic backpack size attempts to solve these problems. Using dynamic size, you can define size profiles, which declare an initial size and max size. You assign size profiles to players via permissions. At the beginning of each wipe, each player's backpack size is reset to the initial size of their profile. Throughout a wipe, players can acquire size upgrades from various sources (i.e., other plugins) up to the max size declared in their profile. For example, if a player's size profile declared an initial size of 6, and a max size of 48, the player could incrementally acquire upgrades until they reach a size of 48, at which point, the backpack could no longer be upgraded.

In order to get started with dynamic backpack size, set the `Dynamic Size (EXPERIMENTAL)` -> `Enabled` config option to `true` and reload the plugin. Then, grant one of the below permissions (or a new one that you define in the config)

The following permissions come with the plugin's **default configuration**. Additional size profiles can be defined in the config, which will generate additional permissions..

- `backpacks.size.profile.6-48` -- 6 to 48 slots
- `backpacks.size.profile.6-96` -- 6 to 96 slots
- `backpacks.size.profile.6-144` -- 6 to 144 slots

**Important notes:**

- If a player is granted multiple `backpacks.size.profile.*` permissions, the last will apply, according to the profile order in the config.
- If a player is granted any `backpacks.size.profile.*` permissions, it will override the default backpack size from the config, as well as any `backpacks.size.<number>` permissions they have been granted because **size profiles take priority over all other configuration/permissions**.
- This plugin does not currently offer built-in features to upgrade backpack capacity, so other plugins must be used for such use cases. Plugin developers can reference the API documentation below for details on how to query and modify backpack capacity. The notable API method names are below.
  - `API_IsDynamicCapacityEnabled`
  - `API_GetBackpackCapacity`
  - `API_GetBackpackInitialCapacity`
  - `API_GetBackpackMaxCapacity`
  - `API_AddBackpackCapacity`
  - `API_SetBackpackCapacity`
- Other plugins can use the `backpack.addsize` and `backpack.setsize` commands, but depending on your exact setup, that could allow for situations where players can effectively waste resources to purchase upgrades when already at max capacity. More precise integrations using the APIs are recommended because other plugins can query the Backpacks plugin to determine whether an upgrade is possible before charging the player.

### Item restriction permissions

If you want to allow backpacks of specific players or groups to accept different items than the default (`Item restrictions` -> `Default ruleset`), then you may do so via permissions. Each ruleset defined in the `Item restrictions` -> `Rulesets by permission` config option will cause the plugin to generate a permission of the format `backpacks.restrictions.<name>`. Granting that permission assigns that ruleset to the corresponding players or groups.

The following permissions come with the plugin's **default configuration**. Additional item restriction rulesets can be defined in the config, which will generate additional permissions.

- `backpacks.restrictions.allowall` -- Allows all items in the player's backpack. Only useful if you have customized the default ruleset to restrict items.

**Note:** If a player is granted multiple `backpacks.restrictions.*` permissions, the last will apply, according to the ruleset order in the config.

### Keep on wipe permissions

If you want to allow backpacks of specific players or groups to retain different items across wipes than the default (`Clear on wipe` > `Default ruleset`), then you may do so via permissions. Each ruleset defined in the `Clear on wipe` > `Rulesets by permission` config option will cause the plugin to generate a permission of the format `backpacks.keeponwipe.<name>`. Granting that permission assigns that ruleset to the corresponding players or groups.

The following permissions come with the plugin's **default configuration**. Additional keep-on-wipe rulesets can be defined in the config, which will generate additional permissions..

- `backpacks.keeponwipe.all` -- Allows all items to be kept across wipes.

**Note:** If a player is granted multiple `backpacks.keeponwipe.*` permissions, the last will apply, according to the ruleset order in the config.

### Legacy permissions

The following permissions are still supported by the plugin for backwards compatibility, but you are advised to use newer permissions instead.

- `backpacks.use.1 - 8` -- Like `backpacks.size.*` but assigns the specified number of rows rather than number of slots.
  - These permissions will be generated when the `"Backpack size"` > `"Enable legacy backpacks.use.1-8 row permissions": true` config option is set, which will be automatically added to your config when upgrading from a previous version of the plugin, if you have the `"Backpack Size (1-8 Rows)"` config option set at that time.
  - Recommended alternative: `backpacks.size.6` through `backpacks.size.48` (present in the default configuration, but might not exist depending on how you have modified the config)
- `backpacks.noblacklist` -- Exempts players from item restrictions, allowing any item to be placed in their backpack.
  - This permission is present when the `"Item restrictions"` > `"Enable legacy noblacklist permission": true` config option is set, which will be automatically added to your config when upgrading from a previous version of the plugin, if you have the `"Use Whitelist (true/false)": true` or `"Use Blacklist (true/false)": true` config options set at that time.
  - Recommended alternative: `backpacks.restrictions.allowall` (present in the default configuration, but might not exist depending on how you have modified the config)
- `backpacks.keeponwipe` -- Exempts players from having their backpack content erased on map wipe.
  - This permission is present when the `"Clear on wipe"` > `"Enable legacy keeponwipe permission": true` config option is set, which will be automatically added to your config when upgrading from a previous version of the plugin, if you have the `"Clear Backpacks on Map-Wipe (true/false)": true` config option set at that time.
  - Recommended alternative: `backpacks.keeponwipe.all` (present in the default configuration, but might not exist depending on how you have modified the config)

## Configuration

Default configuration:

```json
{
  "Backpack size": {
    "Default size": 6,
    "Max size per page": 48,
    "Enable legacy backpacks.use.1-8 row permissions": false,
    "Permission sizes": [
      6,
      12,
      18,
      24,
      30,
      36,
      42,
      48,
      96,
      144
    ],
    "Dynamic Size (EXPERIMENTAL)": {
      "Enabled": false,
      "Reset dynamic size on wipe": {
        "Enabled": true
      },
      "Size profiles": [
        {
          "Permission suffix": "6-48",
          "Initial size": 6,
          "Max size": 48
        },
        {
          "Permission suffix": "6-96",
          "Initial size": 6,
          "Max size": 96
        },
        {
          "Permission suffix": "6-144",
          "Initial size": 6,
          "Max size": 144
        }
      ]
    }
  },
  "Drop on Death (true/false)": true,
  "Erase on Death (true/false)": false,
  "Minimum Despawn Time (Seconds)": 300.0,
  "GUI Button": {
    "Enabled": true,
    "Enabled by default (for players with permission)": true,
    "Skin Id": 3050418770,
    "Image": "",
    "Background Color": "0.969 0.922 0.882 0.035",
    "GUI Button Position": {
      "Anchors Min": "0.5 0.0",
      "Anchors Max": "0.5 0.0",
      "Offsets Min": "185 18",
      "Offsets Max": "245 78"
    }
  },
  "Container UI": {
    "Show page buttons on container bar": false,
    "Max page buttons to show": 8
  },
  "Softcore": {
    "Reclaim Fraction": 0.5
  },
  "Item restrictions": {
    "Enabled": false,
    "Enable legacy noblacklist permission": false,
    "Feedback effect": "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab",
    "Default ruleset": {
      "Allowed item categories": [
        "All"
      ],
      "Disallowed item categories": [],
      "Allowed item short names": [],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    },
    "Rulesets by permission": [
      {
        "Name": "allowall",
        "Allowed item categories": [
          "All"
        ],
        "Disallowed item categories": [],
        "Allowed item short names": [],
        "Disallowed item short names": [],
        "Allowed skin IDs": [],
        "Disallowed skin IDs": []
      }
    ]
  },
  "Clear on wipe": {
    "Enabled": false,
    "Enable legacy keeponwipe permission": false,
    "Default ruleset": {
      "Max slots to keep": 0,
      "Allowed item categories": [],
      "Disallowed item categories": [],
      "Allowed item short names": [],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    },
    "Rulesets by permission": [
      {
        "Name": "all",
        "Max slots to keep": -1,
        "Allowed item categories": [
          "All"
        ],
        "Disallowed item categories": [],
        "Allowed item short names": [],
        "Disallowed item short names": [],
        "Allowed skin IDs": [],
        "Disallowed skin IDs": []
      }
    ]
  },
}
```

### Backpack size

- `Backpack size`
  - `Default size` (Default: `6`) -- Determines the capacity (in slots) of backpacks for players who have the `backpacks.use` permission.
    - Note: Players who have `backpacks.size.<number>` permissions may have greater capacity.
    - Note: Players who have `backpacks.size.profile.*` permissions may have different capacity.
  - `Max size per page` (Default: `48`, Max: `48`) -- Determines the capacity (in slots) per backpack page. For example, if you grant a player `60` backpack capacity, and set the max size per page to `48`, their backpack will have two pages: one page with `48` capacity, and another page with `12` capacity.
  - `Enable legacy backpacks.use.1-8 row permissions` (`true` or `false`; Default: `false`) -- Determines whether the `backpacks.use.1-8` permissions are registered by the plugin. When upgrading the plugin to 3.9+, if you have the `"Backpack Size (1-8 Rows)"` config option set, this option will be automatically enabled for backwards compatibility. Even if you are installing the plugin for the first time, there are valid uses cases to enable this option, such as if you use another plugin that manages those permissions. As of this writing, the XPerience and Backpack Upgrader plugins both use these permissions to allow players to increase capacity through a progression system, but that might have changed by the time you read this.
  - `Permission sizes` -- Each number in this list generates a permission of the format `backpacks.size.<number>`. Granting that permission to a player or group assigns that much capacity to their backpack. This is useful if you want some players to have more capacity than the above default. Note: If a player is granted multiple size permissions, the highest will apply.
    - Note: These permissions are ignored for players who have `backpacks.size.profile.*` permissions.
  - `Dynamic Size (EXPERIMENTAL)` -- Advanced feature for integration with other plugins. **Note: This feature is subject to change in future updates.** If you like the idea of this feature, please provide feedback on the uMod forums (click the "Help" button on the sidebar to access the Backpacks forums).
    - `Enabled` (`true` or `false`; Default: `false`) -- While `true`, dynamic size is enabled, meaning any `backpacks.size.profile.*` permissions will be generated from the config and will take effect. While `false`, dynamic size options are disabled, meaning `backpacks.size.profile.*` permissions will not be generated and will have no effect if previously granted to players.
    - `Reset dynamic size on wipe`
      - `Enabled` (`true` or `false`; Default: `true`) -- While `true`, dynamic size will be reset on wipe. This means all backpacks using dynamic size will revert to initial size.
    - `Size profiles` -- Each entry in this list defines a size profile. When a size profile is assigned to a player via permissions, that player's backpack size is determined by the profile, overriding the default backpack size from the config and any `backpacks.size.<number>` permissions the player has been assigned.
      - `Permission suffix` -- Determines the generated permission of format `backpacks.restrictions.<suffix>`.
      - `Initial size` -- The initial size of the backpack. Other plugins can use the API to change the backpack's size, but not below this amount. The size is automatically reset to this value when the server is wiped.
      - `Max size` -- The max size of the backpack. Other plugins can use the API to increase the backpack's size up to this amount.

### GUI Button

- `GUI Button` -- Determines the display of the GUI button which players can click on to open and close their backpack.
  - `Enabled` (`true` or `false`; Default: `true`) -- Determines whether the GUI button is enabled. If you don't intend to show the GUI button to any players, set this to `false` to improve performance. Disabling this will also unregister the `backpackgui` command so that another plugin can use it.
  - `Enabled by default (for players with permission)` (`true` or `false`; Default: `true`) -- Determines whether the GUI button is shown for new players by default, if they have the `backpacks.gui` permission. Note: Players can use the `backpackgui` command to personally toggle it on/off if they don't agree with the default you chose.
  - `Skin Id` (Default: `3050418770`) -- Determines the skin ID used to display the GUI button, as an alternative to the `Image` URL. While `0`, this has no effect. Note: Users may see the saddle bag item icon briefly while the skin icon is downloaded to their computer.
  - `Image` (Default: `""`) -- Determines the URL of the image to display on the GUI button, as an alternative to `Skin Id`.
  - `Background Color` -- Default: `"0.969 0.922 0.882 0.035"`.
  - `GUI Button Position` -- Determines the position and size of the button.
    - `Anchors Min` -- Default: `"0.5 0.0"` (bottom center of screen). Don't change this unless you know what you are doing.
    - `Anchors Max` -- Default: `"0.5 0.0"` (bottom center of screen). Don't change this unless you know what you are doing.
    - `Offsets Min` -- Determines the bottom-left position of the button. Default: `"185 18"` (right side of belt).
    - `Offsets Max` -- Determines the top-right position of the button. Default: `"245 78"` (right side of belt).

### Container UI

- `Container UI` -- Controls the display of the backpack container UI.
  - `Show page buttons on container bar` (`true` or `false`; Default: `false`) -- Determines whether the page buttons (e.g., 1, 2, 3) are shown on the container bar or above the container bar. By default, they are shown above the container bar, to allow for compatibility with the Sort Button plugin. However, if you are using the Quick Sort plugin and want to allow 48 slots of capacity per backpack page, you probably want to enable this option in order to move the page buttons down.
  - `Max page buttons to show` -- Determines the max number of page buttons (e.g., 1, 2, 3) that can be shown at once. When the player's backpack has more pages than configured here, additional `<` and `>` buttons will shown. 

**Recommended for [Sort Button](https://umod.org/plugins/sort-button) (default):**

![](https://raw.githubusercontent.com/LaserHydra/Backpacks/master/Images/PageButtonsAboveBar.png)

**Recommended for [Quick Sort](https://umod.org/plugins/quick-sort):**

![](https://raw.githubusercontent.com/LaserHydra/Backpacks/master/Images/PageButtonsOnBar.png)

### Item restrictions

- `Item restrictions`
  - `Enabled` -- Determines whether player backpacks are subject to item restrictions. Set to `false` to disable item restrictions for all players. Set to `true` to make the below rulesets apply. Note: Regardless of these settings, other plugins can prevent specific items from being added to Backpacks using the `CanBackpackAcceptItem` hook.
  - `Enable legacy noblacklist permission` (`true` or `false`; Default: `false`) -- Determines whether the `backpacks.noblacklist` permission is registered by the plugin. When upgrading the plugin to v3.9+, if you have the `"Use Whitelist (true/false)": true` or `"Use Blacklist (true/false)": true` config options set, this option will be automatically enabled for backwards compatibility. 
  - `Feedback effect` -- The effect prefab to play when the player tries to add a disallowed item to their backpack. Set to `""` to disable the effect.
  - `Default ruleset` -- The default ruleset applies to all players' backpacks, except for players who have been granted `backpacks.restrictions.<name>` permissions (which are generated via `Rulesets by permission` below).
    - `Allowed item categories` -- Determines which item categories are allowed in backpacks that are assigned this ruleset, **in addition** to any allowed item short names and skin IDs.
      - If you want to allow only specific item short names, leave this option blank (`[]`) and instead add those item short names to `Allowed item short names`.
      - If you want to allow most items, with specific exceptions, set this option to `"Allowed item categories": ["All"]`, then use the `Disallowed item categories`, `Disallowed item short names`, and/or `Disallowed skin IDs` options.
      - Allowed values: `"All"`, `"Ammunition"`, `"Attire"`, `"Common"`, `"Component"`, `"Construction"`, `"Electrical"`, `"Favourite"`, `"Food"`, `"Fun"`, `"Items"`, `"Medical"`, `"Misc"`, `"Resources"`, `"Search"`, `"Tool"`, `"Traps"`, `"Weapon"`.
    - `Disallowed item categories` -- Determines which item categories are disallowed in backpacks that are assigned this ruleset.
      - The only correct way to use this option is by setting `"Allowed item categories": ["All"]` and using this option to specify categories that you want to disallow.
      - If an item is in a disallowed category, that item may still be allowed if it's short name is specified in `Allowed item short names`, or if it's skin ID is specified in `Allowed skin IDs`.
      - Allowed values: Same as for `Allowed item categories`.
    - `Allowed item short names` -- Determines which item short names are allowed in backpacks that are assigned this ruleset, **in addition** to any allowed item categories and skin IDs.
      - Item short names are evaluated with higher priority than item categories, meaning you can use this option to allow items that are in disallowed categories.
    - `Disallowed item short names` -- Determines which item short names are disallowed in backpacks that are assigned this ruleset.
      - This option has no effect if `Allowed item categories` is blank because that means no items are allowed.
      - If an item has a disallowed short name, that item may still be allowed if it's skin ID is specified in `Allowed skin IDs`.
    - `Allowed skin IDs` -- Determines which item skin IDs are allowed in backpacks that are assigned this ruleset, **in addition** to any allowed item categories and short names.
      - Item skin IDs are evaluated with higher priority than item categories and item short names, meaning you can use this option to allow items that have disallowed categories or short names.
    - `Disallowed skin IDs` -- Determines which item skin IDs are disallowed in backpacks that are assigned this ruleset.
      - This option has no effect if both `Allowed item categories` and `Allowed item short names` are blank because that means no items are allowed.
      - If an item has a disallowed skin ID, it will not be allowed under any circumstances, even if that item has an allowed category or short name.
  - `Rulesets by permission` -- Each ruleset in this list generates a permission of the format `backpacks.restrictions.<name>`. Besides the `Name` option, the rest of the options are the same as for `Default ruleset`.

Example rulesets:

```json
"Rulesets by permission": [
  {
    "Name": "no_fun_except_trumpet",
    "Allowed item categories": [
      "All"
    ],
    "Disallowed item categories": [
      "Fun"
    ],
    "Allowed item short names": [
      "fun.trumpet"
    ],
    "Disallowed item short names": [],
    "Allowed skin IDs": [],
    "Disallowed skin IDs": []
  },
  {
    "Name": "no_c4",
    "Allowed item categories": [
      "All"
    ],
    "Disallowed item categories": [],
    "Allowed item short names": [],
    "Disallowed item short names": [
      "explosive.timed"
    ],
    "Allowed skin IDs": [],
    "Disallowed skin IDs": []
  },
  {
    "Name": "only_food_but_no_apples",
    "Allowed item categories": [
      "Food"
    ],
    "Disallowed item categories": [],
    "Allowed item short names": [],
    "Disallowed item short names": [
      "apple",
      "apple.spoiled"
    ],
    "Allowed skin IDs": [],
    "Disallowed skin IDs": []
  }
]
```

### Clear backpacks on wipe

- `Clear on wipe`
  - `Enabled` (Default: `false`) -- While `true`, the contents of player backpacks may be cleared when the map is wiped, according to the below rulesets.
    - Note: In order for this feature to work, Oxide must be installed and the plugin must be loaded when the server boots for the first time with the new map. In rare cases, server owners make the mistake of booting their server after a Rust update without Oxide installed or with the plugin out of date (e.g., failing to compile), causing backpacks to not be wiped. If you make this mistake, it is advised that you wipe your server again by deleting your server save file and restarting the server again, because other plugins that rely on detecting map wipes would have also been affected. Alternatively, you may manually wipe all backpacks by unloading the plugin, deleting the `oxide/data/Backpacks` directory, then loading the plugin.
  - `Enable legacy keeponwipe permission` (`true` or `false`; Default: `false`) -- Determines whether the `backpacks.keeponwipe` permission is registered by the plugin. When upgrading the plugin to v3.9+, if you have the `"Clear Backpacks on Map-Wipe (true/false)": true` config option set, this option will be automatically enabled for backwards compatibility. That permission is superseded by the `backpacks.keeponwipe.all` permission.
  - `Default ruleset` -- The default ruleset applies to all players' backpacks, except for players who have been granted `backpacks.keeponwipe.<name>` permissions (which are generated via `Rulesets by permission` below).
    - `Max slots to keep` -- Determines how many item slots may be kept across wipes. Set to `-1` to keep unlimited item slots, up to the size of the backpack.
      - For example, if you set this to `10`, when a wipe occurs, the plugin will keep the first `10` allowed items in the backpack, prioritizing items toward the beginning of the backpack, and the rest of the items will be erased.
      - Items that are disallowed are skipped and do not count toward this limit.
      - Empty slots are skipped and do not count toward this limit.
    - `Allowed item categories` -- Determines which item categories are allowed to be kept across wipes, **in addition** to any allowed item short names and skin IDs.
      - If you want to allow only specific item short names, leave this option blank (`[]`) and instead add those item short names to `Allowed item short names`.
      - If you want to allow most items, with specific exceptions, set this option to `"Allowed item categories": ["All"]`, then use the `Disallowed item categories`, `Disallowed item short names`, and/or `Disallowed skin IDs` options.
      - Allowed values: `"All"`, `"Ammunition"`, `"Attire"`, `"Common"`, `"Component"`, `"Construction"`, `"Electrical"`, `"Favourite"`, `"Food"`, `"Fun"`, `"Items"`, `"Medical"`, `"Misc"`, `"Resources"`, `"Search"`, `"Tool"`, `"Traps"`, `"Weapon"`.
    - `Disallowed item categories` -- Determines which item categories are disallowed from being kept across wipes.
      - The only correct way to use this option is by setting `"Allowed item categories": ["All"]` and using this option to specify categories that you want to disallow.
      - If an item is in a disallowed category, that item may still be allowed if it's short name is specified in `Allowed item short names`, or if it's skin ID is specified in `Allowed skin IDs`.
      - Allowed values: Same as for `Allowed item categories`.
    - `Allowed item short names` -- Determines which item short names are allowed to be kept across wipes, **in addition** to any allowed item categories and skin IDs.
      - Item short names are evaluated with higher priority than item categories, meaning you can use this option to allow items that are in disallowed categories.
    - `Disallowed item short names` -- Determines which item short names are disallowed from being kept across wipes.
      - This option has no effect if `Allowed item categories` is blank.
      - If an item has a disallowed short name, that item may still be allowed if it's skin ID is specified in `Allowed skin IDs`.
    - `Allowed skin IDs` -- Determines which item skin IDs are allowed to be kept across wipes, **in addition** to any allowed to any item categories and short names.
      - Item skin IDs are evaluated with higher priority than item categories and item short names, meaning you can use this option to allow items that have disallowed categories or short names.
    - `Disallowed skin IDs` -- Determines which item skin IDs are disallowed from being kept across wipes.
      - This option has no effect if both `Allowed item categories` and `Allowed item short names` are blank.
      - If an item has a disallowed skin ID, it will not be allowed under any circumstances, even if that item has an allowed category or short name.
  - `Rulesets by permission` -- Each ruleset in this list generates a permission of the format `backpacks.keeponipe.<name>`. Besides the `Name` option, the rest of the options are the same as for `Default ruleset`.

Example rulesets:

```json
"Clear on wipe": {
  "Enabled": true,
  "Default ruleset": {
    "Max slots to keep": 0,
    "Allowed item categories": [],
    "Disallowed item categories": [],
    "Allowed item short names": [],
    "Disallowed item short names": [],
    "Allowed skin IDs": [],
    "Disallowed skin IDs": []
  },
  "Rulesets by permission": [
    {
      "Name": "all",
      "Max slots to keep": -1,
      "Allowed item categories": [
        "All"
      ],
      "Disallowed item categories": [],
      "Allowed item short names": [],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    },
    {
      "Name": "1row",
      "Max slots to keep": 6,
      "Allowed item categories": [
        "All"
      ],
      "Disallowed item categories": [],
      "Allowed item short names": [],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    },
    {
      "Name": "2rows",
      "Max slots to keep": 12,
      "Allowed item categories": [
        "All"
      ],
      "Disallowed item categories": [],
      "Allowed item short names": [],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    },
    {
      "Name": "onlywood",
      "Max slots to keep": -1,
      "Allowed item categories": [],
      "Disallowed item categories": [],
      "Allowed item short names": [
        "wood"
      ],
      "Disallowed item short names": [],
      "Allowed skin IDs": [],
      "Disallowed skin IDs": []
    }
  ]
},
```

### Miscellaneous options

- `Drop on Death (true/false)` (Default: `true`) -- While `true`, the contents of players' backpacks will be dropped in a backpack by their corpse when they die. When the player respawns, their backpack will be empty, but can find their dropped backpack to recover its contents.
  - Note: Even while this option is enabled, players with the `backpacks.keepondeath` permission will keep their backpack contents on death.
  - Note: Even if dropping backpacks is disabled, other plugins such as Raidable Bases may forcibly drop the player's backpack if configured to do so, via the `API_DropBackpack` API.
- `Erase on Death (true/false)` (Default: `false`) -- While `true`, the contents of players' backpacks will be erased when they die. Erased backpack contents cannot be recovered under any circumstances.
  - Note: Even while this option is enabled, players with the `backpacks.keepondeath` permission will keep their backpack contents on death.
- `Minimum Despawn Time (Seconds)` (Default: `300.0`) -- Determines the minimum time (in seconds) that dropped backpacks will be protected from despawning. If the backpack contents are moderately rare, as determined by vanilla Rust, the backpack may take longer to despawn than this duration.
- `Softcore` -- Determines options for Softcore mode.
  - `Reclaim Fraction` (Default: `0.5`, Min: `0.0`, Max: `1.0`) -- Determines the percentage of backpack items that are sent to the reclaim terminal when you die, if drop on death is enabled. Note: Items sent from your backpack to the reclaim terminal are **not** accessible at your corpse.

## Localization

## FAQ

#### Why are backpacks not dropping when players die?

There are three possible reasons backpacks won't drop.

1. The configuration option `"Drop on Death (true/false)"` is `false`.
2. Players have the `backpacks.keepondeath` permission.
3. Another plugin prevented the backpack from dropping via the `CanDropBackpack` hook. Arena plugins will often do this for players in the arena.

#### How do I move the backpack button to the left side of the hotbar?

Replace the `"GUI Button Position"` section of the plugin configuration with the following.

```json
"GUI Button Position": {
  "Anchors Min": "0.5 0.0",
  "Anchors Max": "0.5 0.0",
  "Offsets Min": "-265 18",
  "Offsets Max": "-205 78"
}
```

## Developer API

### API_IsDynamicCapacityEnabled

```csharp
int API_IsDynamicCapacityEnabled()
```

Returns `true` if dynamic capacity is enabled in the config, else returns `false`. This is useful for addon plugins that utilize dynamic capacity, as it allows them to print a warning in the server console informing the administrator that they will need to enable that config option in order to use dynamic capacity features provided by the addon plugin.

### API_GetBackpackCapacity

```csharp
int API_GetBackpackCapacity(BasePlayer player)
```

```csharp
int API_GetBackpackCapacityById(ulong playerID, string playerIDString)
```

Returns the player's current backpack capacity.

### API_GetBackpackInitialCapacity

```csharp
int API_GetBackpackInitialCapacity(BasePlayer player)
```

Returns the player's initial/minimum backpack capacity. If dynamic size is not enabled, or if the player does not have an assigned size profile, the return value will be simply the player's current backpack capacity.

### API_GetBackpackMaxCapacity

```csharp
int API_GetBackpackMaxCapacity(BasePlayer player)
```

Returns the player's max backpack capacity. If dynamic size is not enabled, or if the player does not have an assigned size profile, the return value will be simply the player's current backpack capacity.

### API_AddBackpackCapacity

```csharp
int API_AddBackpackCapacity(BasePlayer player, int amount)
```

Attempts to increase the player's backpack capacity by the specified amount, without going outside the range defined by the player's size profile (i.e., determined by their `backpacks.size.profile.*` permissions). A negative number can be used to decrease backpack capacity. If the player does not have a size profile assigned, this will have no effect. Returns the player's backpack capacity after the change has been applied.

### API_SetBackpackCapacity

```csharp
int API_SetBackpackCapacity(BasePlayer player, int amount)
```

Attempts to set the player's backpack capacity to the specified amount, without going outside the range defined by the player's size profile (i.e., determined by their `backpacks.size.profile.*` permissions). If the player does not have a size profile assigned, this will have no effect. Returns the player's backpack capacity after the change has been applied.

### API_GetBackpackItemAmount

```csharp
int API_GetBackpackItemAmount(ulong backpackOwnerID, int itemId, ulong skinId = 0)
```

Returns the quantity of a given item in the player's backpack. Returns `0` if the player has no backpack. This API is more performant than `API_GetBackpackContainer` because it does not require creating the backpack container.

### API_DropBackpack

```csharp
DroppedItemContainer API_DropBackpack(BasePlayer player, List<DroppedItemContainer> collect = null)
```

Drop the player's backpack at their current position. This can be used, for example, to only drop the player's backpack when they die in a PvP zone.

If the `collect` list is provided, every dropped container for the backpack will be added to that list. This is useful if the backpack has multiple pages.

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

### API_MutateBackpackItems

```csharp
int API_MutateBackpackItems(ulong ownerId, Dictionary<string, object> itemQuery, Dictionary<string, object> mutationRequest)
```

Finds and mutates items in the player's backpack. The return value is the number of item stacks that were mutated.

- `ownerId` -- The Steam ID of the player's backpack.
- `itemQuery` -- Describes the items to find. See the "Item queries" section below for details.
- `mutationRequest` -- Describes the mutations to apply. Supports `"ItemId"` (int), `"SkinId"` (ulong) and `"DisplayName"` (string). More parameters can be added on request. Note: Changing `ItemId` should be done only for equivalent item types, such as a Nomad hazmat suit to an Arctic hazmat suit, or else various there may be unpredictable consequences.

Example:

```csharp
int itemsReskinned = Backpacks.Call<int>("API_MutateBackpackItems", basePlayer.userID,
    // Find items with where `item.info.itemid == 1234567890`
    new Dictionary<string, object>{ ["ItemId"] = 1234567890 },
    // Change `item.skin` to `9876543210`
    new Dictionary<string, object>{ ["SkinId"] = 9876543210 }
);
basePlayer.ChatMessage($"{itemsReskinned} item stacks were reskinned in the backpack.");
```

### API_GetExistingBackpacks (DEPRECATED)

**It is strongly advised that plugins do not use `API_GetExistingBackpacks` because a backpack can have multiple containers if it has multiple pages, and can have virtual representations of containers that have not yet been accessed.** If you think your plugin has a valid reason to access backpack containers, please open a support thread to discuss your use case.

```csharp
Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
```

Returns all backpack containers that are cached in the plugin's memory, keyed by the Steam IDs of the backpack owners. This was originally contributed so that item cleaner plugins could determine which items were in backpacks in order to ignore them. However, as of Backpacks v3.7.0, all item cleaner plugins should automatically be compatible.

### API_GetBackpackContainer (DEPRECATED)

**It is strongly advised that plugins do not use `API_GetBackpackContainer` because a backpack can have multiple containers if it has multiple pages, and can have virtual representations of containers that have not yet been accessed.** If you think your plugin has a valid reason to access backpack containers, please open a support thread to discuss your use case.

```csharp
ItemContainer API_GetBackpackContainer(ulong backpackOwnerID)
```

Returns a reference to the underlying `ItemContainer` of a player's backpack. Returns `null` if the player essentially has no backpack (no data file and no backpack in-memory).

Notes:
- This will create the container entity if it doesn't exist. This can add load to the server, so it's recommended to use this API only if the other API methods do not meet your needs. For example, if you want to know only the quantity of an item in the player's backpack, you can use `API_GetBackpackItemAmount` which can count the items without creating the container.
- You should avoid caching the container because several events may cause the backpack's underlying container to be replaced or deleted, which would make the cached reference useless.

### Item queries

Multiple API methods, including `API_MutateBackpackItems` support item queries. An item query is an abstract description of items that you are interested in. Each item query is a `Dictionary<string, object>` and supports the below keys. When an item query has multiple parameters, an item will only be considered a match if it satisfies them all.

- `"BlueprintId"`: `int` -- Match items where `item.blueprintTarget` equals the specified value. For example, to find Auto Turret blueprints, provide the Auto Turret item id to this parameter.
- `"DisplayName"`: `string` -- Match items where `item.name` equals this value (case insensitive).
- `"DataInt"`: `int` -- Match items where `item.instanceData.DataInt` equals this value.
- `"FlagsContain"`: `Item.Flag` -- Match items where the `item.flags` bit mask contains all of the bits in the provided bit mask. This can be used to find items that have one or more flags, without caring about whether the items have additional flags.
- `"FlagsEqual"`: `Item.Flag` -- Match items where `item.flags` equals this value. This can be used to find items that have exactly all the flags you are interested in with no additional flags.
- `"ItemDefinition"`: `ItemDefinition` -- Match items where `item.info` equals this value. Alternative to `"ItemId"`.
- `"ItemId"`: `int` -- Matches items where `item.info.itemid` equals this value. Alternative to `"ItemDefinition"`.
- `"MinCondition"`: `float` -- Matches items where `item.conditionNormalized` is greater than or equal to this value. This parameter is ignored if the item does not support condition (according to `item.info.condition.enabled`).
- `"RequireEmpty"`: `bool` -- Matches items that do not have child contents (i.e., `item.contents?.itemList?.Count` is `0` or `null`). For example, while `true`, a weapon with an attachment would not match, but a weapon without an attachment could match.
- `"SkinId"`: `ulong` -- Match items where `item.skin` equals the specified value.

The concept of item queries exists because the Backpacks plugin does not necessarily use actual items or containers for a given player's backpack, or for a given page of a player's backpack, until absolutely needed (e.g., when the player opens that page). Item queries allow other plugins to query, modify, and even take items from a backpack without being concerned with the internal implementation details of the Backpacks plugin.

## Developer Hooks

### CanOpenBackpack

```csharp
object CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
```

- Called when a player tries to open a backpack.
- Called when gather mode tries to automatically deposit items into the backpack. The result is cached per backpack per frame to reduce performance cost.
- Called when the Item Retriever plugin attempts to automatically take items from the player's backpack. The result is cached per backpack per frame to reduce performance cost.

Returning `false` or a `string` will prevent the action. If the player was attempting to open the backpack (as opposed to using gather or retrieve) and a `string` was returned, that `string` will be send as a chat message to the player.

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

### OnBackpackDropped

```csharp
void OnBackpackDropped(ulong ownerId, List<DroppedItemContainer> droppedBackpackList)
```

- Called after a player's backpack has been dropped.
- The `List<DroppedItemContainer>` list contains all dropped backpack containers. Multiple containers may be dropped depending on how many items are in the player's backpack. Note that this list is pooled and will be immediately cleared after the hook call has finished, so if you need to process the containers after the hook call, you should copy the contents of the list to a list that you control.  

### CanEraseBackpack

```csharp
bool CanEraseBackpack(ulong backpackOwnerID)
```

- Called when a player dies while the `"Erase on Death (true/false)"` option is set to `true`.
- Returning `false` prevents the backpack from being erased.
