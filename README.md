**Backpacks** allows players to have backpacks that provide them with extra inventory space. The backpack can be dropped on death.

**Note:** To bind a key to open the backpack, use: `bind b backpack.open` in your F1 client console.

## Donations

Please consider donating to support me and help me put more time into my plugins. You can donate, by clicking [here](https://laserhydra.com/donate).

## Chat Commands

- `/backpack` -- Open your own backpack
- `/viewbackpack <steamid>` -- Open another players backpack [Admin Command]

## Permissions

- `backpacks.admin` -- Required for `/viewbackpack` command
- `backpacks.use` -- Required for using a backpack
- `backpacks.use.small` -- Makes player have a small backpack *(still requires backpacks.use)*
- `backpacks.use.medium` -- Makes player have a medium backpack (still requires backpacks.use)*
- `backpacks.use.large` -- Makes player have a large backpack *(still requires backpacks.use)*

## Configuration

```json
{
  "Backpack Size (1-3)": 2,
  "Blacklisted Items (Item Shortnames)": [
    "rocket.launcher",
    "lmg.m249"
  ],
  "Drop On Death": true,
  "Erase On Death": false,
  "Hide On Back If Empty": true,
  "Show On Back": true,
  "Use Blacklist": false
}
```

## Localization

The default messages are in the `Backpacks.json` file under the `oxide/lang/en` directory. To add support for another language, create a new language folder (ex. de for German) if not already created, copy the default language file to the new folder, and then customize the messages.
