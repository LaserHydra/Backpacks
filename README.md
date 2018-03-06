**Backpacks** allows players to have backpacks that provide them with extra inventory space. The backpack can be dropped on death.

***To bind a key to open the backpack, use: "bind b backpack.open" in your F1 client console.***

**Donations:**
Please consider donating to support me and help me put more time into my plugins.
You can donate, by clicking [here](https://laserhydra.com/donate).

## Preview
![preview](https://i.gyazo.com/99f831d0d9d44c2cc4b25f8c3574270c.jpg)

## Chat Commands
- **`/backpack`** -- to open your own backpack
- **`/viewbackpack <steamid>`** -- to open another players backpack [Admin Command]

## Permissions
- **`backpacks.admin`** -- required for /viewbackpack command
- **`backpacks.use`** -- required for using a backpack
- **`backpacks.use.small`** -- makes player have a small backpack (still requires backpacks.use)
- **`backpacks.use.medium`** -- makes player have a medium backpack (still requires backpacks.use)
- **`backpacks.use.large`** -- makes player have a large backpack (still requires backpacks.use)

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
```json
{
  "No Permission": "You don't have permission to use this command.",
  "Backpack Already Open": "Somebody already has this backpack open!",
  "May Not Open Backpack In Event": "You may not open a backpack while participating in an event!"
}
```