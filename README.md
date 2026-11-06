# MHDBEditor

**A Windows tool for editing an MHServerEmu database without touching SQL.**

We run an MHServerEmu server, and fixing a player used to mean hand-editing SQLite blobs and
hoping nothing broke. So we built this. Open your `Account.db`, find a player, fix them, save.
Mwuahahaha.

It works with both our COA build and stock MHServerEmu (game versions 1.52 and 1.53).

---

## What it does

### Simple Mode (the tab it opens on)
Type a player's name or email, pick them, then do the everyday stuff:
- give items (from templates you save) or Gazillionite
- set a hero's level
- move a player who's stuck on a loading screen
- reset a password

### Accounts
- rename, change email, set user level
- ban, unban, whitelist, bypass queue
- reset password, clear machine id
- export an account to a `.sav` file and import it back, or clone it as a new account

### Items and heroes
- browse every hero, team-up, item and pet a player owns
- edit item properties, rarity, level and affixes, with real names instead of hex ids
- see what an item's affixes actually do, and spot the broken ones: a stat over its cap, an
  affix that does nothing, or one that stops the rest of the item from working
- move, copy or delete items, and save any item as a template to hand out later

### Player data
Missions, stash tabs (names, colors, order), achievements, map discovery, game options, friends,
vendors and difficulty. View them, and reset or complete what you need.

### Guilds and hardware bans
Rename guilds, edit the message of the day, manage members, and ban or unban hardware ids.

### Database Health Check
Scans the whole database for things that will bite you later: items with no owner, two items in
one slot, unreadable data, broken affixes, bad start locations, and leftover guild or player rows.
Most of it fixes with one click.

### Search & Bulk Actions
Find items or players across the whole server with dropdowns, no typing needed. Search by
category, item, affix, rarity, level, owner or affix problem. Then tick the results and act on
all of them at once: take items back, give items or Gazillionite, move players, ban, or export
to CSV.

---

## Getting started

1. You need Windows and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Build it with `dotnet build`, or open `MHDBEditor.csproj` in Visual Studio.
3. Run it, then **File > Open Database...** and pick your server's `Account.db`.
4. Optional, but you'll want it: load the game data (your server's `Data/Game/Calligraphy.sip`).
   That turns hex ids into real names and turns on the affix checks.

## Before you edit

- **Back up first.** There's a **Create Backup Now** button. Use it. Bulk actions offer to
  back up too.
- **Server running?** You can edit players who are logged off. Wait until the server log says
  their player handle was removed. Don't edit anyone who's online, because the server will
  overwrite your change when they log out.
- **Guild changes need a server restart.** The server keeps guilds in memory.

## Built with

C# / WinForms on .NET 8, [Dapper](https://github.com/DapperLib/Dapper),
[System.Data.SQLite](https://system.data.sqlite.org/) and
[K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4).

This is a fan tool for [MHServerEmu](https://github.com/Crypto137/MHServerEmu). It isn't made by
or connected to Marvel, Gazillion or the MHServerEmu team.
