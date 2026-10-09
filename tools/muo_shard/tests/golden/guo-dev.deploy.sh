#!/usr/bin/env bash
# muo_shard deploy for profile guo-dev. Generated: review it, then run it on the host as root.
# It is idempotent and holds no secret; those live in /etc/muo/guo-dev.env.
set -euo pipefail
die() { echo "muo_shard: $*" >&2; exit 1; }
[ "$(id -u)" = 0 ] || die "run this as root"
ID=guo-dev
SVC_USER=muo-guo-dev
BASE=/srv/muo/guo-dev
SRC=$BASE/src
DIST=$BASE/dist
ENV_FILE=/etc/muo/guo-dev.env
UNIT=muo-guo-dev.service
PORT=2593

PIN=d4531cd94b739613155225c234900de9f47d2c88
CLONE_URL=https://github.com/modernuo/ModernUO.git

as_user() { runuser -u "$SVC_USER" -- env HOME="$BASE" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$@"; }
have_systemd() { [ -d /run/systemd/system ]; }

# preflight: bootstrap ran, the host values are there
[ -d "$BASE" ] && [ -f "$ENV_FILE" ] || die "run bootstrap first"
set -a; . "$ENV_FILE"; set +a
: "${MUO_CLIENT_DATA:?MUO_CLIENT_DATA is not set in $ENV_FILE}"
[ -d "$MUO_CLIENT_DATA" ] || die "MUO_CLIENT_DATA is not a folder on this host"

# stop the shard before its files change
if have_systemd && systemctl is-active --quiet "$UNIT"; then systemctl stop "$UNIT"; fi

# patches (embedded)
install -d "$(dirname $BASE/patches/0001-headless-owner-account.patch)"
cat > $BASE/patches/0001-headless-owner-account.patch <<'MUO_EOF_0'
MUO patch, ours: a GUO patch to ModernUO, applied by launchers\shard\fetch.bat
and tools/muo_shard. Listed with its verdict in tools/modernuo/UPSTREAM.md; the
upstream-ready half is upstream/0001-headless-owner-account.patch.

Headless boot makes the owner account (and the game master accounts) from the
environment. An existing account is raised only if it already holds the
configured password (MV1), and a published default password (blank, the old
"guoprobe", the account's own name) never makes or raises one.

diff --git a/Projects/UOContent/Misc/AccountPrompt.cs b/Projects/UOContent/Misc/AccountPrompt.cs
index 032c55526..69b1eac2c 100644
--- a/Projects/UOContent/Misc/AccountPrompt.cs
+++ b/Projects/UOContent/Misc/AccountPrompt.cs
@@ -1,3 +1,4 @@
+using System;
 using Server.Accounting;
 using Server.Logging;
 
@@ -9,9 +10,22 @@ public static class AccountPrompt
 
     public static void Initialize()
     {
+        // GUO patch: headless startup used to throw at the prompt below --
+        // Core.Headless is true whenever stdin is redirected, which is every
+        // scripted run. The owner account is made from the environment
+        // instead, so a dev shard can be generated and administered from the
+        // client without anyone typing at the console.
+        if (Core.Headless)
+        {
+            EnsureOwnerAccountFromEnvironment();
+            EnsureGmAccountsFromEnvironment();
+            return;
+        }
+
         if (Accounts.Count == 0)
         {
             logger.Warning("This server has no accounts.");
+
             logger.Information("Do you want to create the owner account now? (y/n):");
 
             var answer = ConsoleInputHandler.ReadLine();
@@ -37,4 +51,182 @@ public static class AccountPrompt
             }
         }
     }
+
+    // GUO patch.
+    private static void EnsureOwnerAccountFromEnvironment()
+    {
+        var username = Environment.GetEnvironmentVariable("UO_SHARD_OWNER");
+        var password = Environment.GetEnvironmentVariable("UO_SHARD_OWNER_PASSWORD");
+
+        // No default password: GUO's configure.py generates one per user.
+        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
+        {
+            logger.Warning("Headless: UO_SHARD_OWNER or UO_SHARD_OWNER_PASSWORD is not set, so there is no owner account.");
+            return;
+        }
+
+        if (IsDefaultPassword(username, password))
+        {
+            logger.Warning("Headless: UO_SHARD_OWNER_PASSWORD is a published default, so there is no owner account.");
+            return;
+        }
+
+        // Auto account creation means the account usually exists already,
+        // made by the first login and therefore only a player.
+        if (Accounts.GetAccount(username) is Account existing)
+        {
+            // With auto account creation on, anyone can log in as UO_SHARD_OWNER
+            // before the owner does. Raising that account blind would hand
+            // them the shard, so an account below owner is raised only if it
+            // holds the owner's password. One that is owner already takes the
+            // configured password. The password is never logged.
+            if (existing.AccessLevel < AccessLevel.Owner)
+            {
+                if (!existing.CheckPassword(password))
+                {
+                    logger.Warning(
+                        "Headless: account '{Username}' exists but does not hold UO_SHARD_OWNER_PASSWORD; it is left as it is.",
+                        username
+                    );
+                    return;
+                }
+
+                existing.AccessLevel = AccessLevel.Owner;
+                logger.Information("Headless: raised account '{Username}' to owner.", username);
+            }
+            else
+            {
+                EnsurePassword(existing, password);
+            }
+
+            // A character's access level is copied from the account when the
+            // character is made, and never again -- so a character created
+            // before the account was raised stays a player, and commands typed
+            // by it are silently treated as speech.
+            for (var i = 0; i < existing.Length; i++)
+            {
+                if (existing[i] is { } character && character.AccessLevel < AccessLevel.Owner)
+                {
+                    character.AccessLevel = AccessLevel.Owner;
+                    logger.Information("Headless: raised character '{Name}' to owner.", character.Name);
+                }
+            }
+
+            ServerAccess.AddProtectedAccount(existing, true);
+            return;
+        }
+
+        var account = new Account(username, password)
+        {
+            AccessLevel = AccessLevel.Owner
+        };
+
+        logger.Information("Headless: owner account created: {Username}", username);
+        ServerAccess.AddProtectedAccount(account, true);
+    }
+
+    // GUO patch: accounts for scripted clients that run alongside the owner.
+    // The shard refuses a second character from one account, so a run that
+    // drives several clients at once needs several accounts, and most of
+    // what those clients do ("[go", "[globallight") takes staff access.
+    // Comma-separated names; they share UO_SHARD_GM_PASSWORD, which GUO's
+    // configure.py generates per user. No password, no accounts.
+    private static void EnsureGmAccountsFromEnvironment()
+    {
+        var list = Environment.GetEnvironmentVariable("UO_SHARD_GM_ACCOUNTS");
+        var password = Environment.GetEnvironmentVariable("UO_SHARD_GM_PASSWORD");
+
+        if (string.IsNullOrWhiteSpace(list))
+        {
+            return;
+        }
+
+        if (string.IsNullOrWhiteSpace(password))
+        {
+            logger.Warning("Headless: UO_SHARD_GM_PASSWORD is not set, so no game master accounts.");
+            return;
+        }
+
+        if (IsDefaultPassword(null, password))
+        {
+            logger.Warning("Headless: UO_SHARD_GM_PASSWORD is a published default, so no game master accounts.");
+            return;
+        }
+
+        foreach (var raw in list.Split(',', ';', ' '))
+        {
+            var username = raw.Trim();
+
+            if (username.Length == 0)
+            {
+                continue;
+            }
+
+            if (IsDefaultPassword(username, password))
+            {
+                logger.Warning("Headless: UO_SHARD_GM_PASSWORD is the name of '{Username}', so it is skipped.", username);
+                continue;
+            }
+
+            // The same rule as the owner: an account below game master is
+            // raised only if it already holds the game master password.
+            if (Accounts.GetAccount(username) is Account existing)
+            {
+                if (existing.AccessLevel < AccessLevel.GameMaster)
+                {
+                    if (!existing.CheckPassword(password))
+                    {
+                        logger.Warning(
+                            "Headless: account '{Username}' exists but does not hold UO_SHARD_GM_PASSWORD; it is left as it is.",
+                            username
+                        );
+                        continue;
+                    }
+
+                    existing.AccessLevel = AccessLevel.GameMaster;
+                    logger.Information("Headless: raised account '{Username}' to game master.", username);
+                }
+                else
+                {
+                    EnsurePassword(existing, password);
+                }
+
+                for (var i = 0; i < existing.Length; i++)
+                {
+                    if (existing[i] is { } character && character.AccessLevel < AccessLevel.GameMaster)
+                    {
+                        character.AccessLevel = AccessLevel.GameMaster;
+                        logger.Information("Headless: raised character '{Name}' to game master.", character.Name);
+                    }
+                }
+
+                continue;
+            }
+
+            _ = new Account(username, password)
+            {
+                AccessLevel = AccessLevel.GameMaster
+            };
+
+            logger.Information("Headless: game master account created: {Username}", username);
+        }
+    }
+
+    // GUO patch: the configured password is the account's password, so a
+    // shard made before the passwords were generated stops taking the old one.
+    private static void EnsurePassword(Account account, string password)
+    {
+        if (!account.CheckPassword(password))
+        {
+            account.SetPassword(password);
+            logger.Information("Headless: password of account '{Username}' set from the environment.", account.Username);
+        }
+    }
+
+    // GUO patch: passwords anyone can read in GUO's history -- the old
+    // config.bat default, and an account's own name (the old game master
+    // rule) -- never make or raise a staff account.
+    private static bool IsDefaultPassword(string username, string password) =>
+        string.Equals(password, "guoprobe", StringComparison.OrdinalIgnoreCase) ||
+        username != null && string.Equals(password, username, StringComparison.OrdinalIgnoreCase);
 }
MUO_EOF_0
chmod 0644 $BASE/patches/0001-headless-owner-account.patch
install -d "$(dirname $BASE/patches/0002-settable-update-range.patch)"
cat > $BASE/patches/0002-settable-update-range.patch <<'MUO_EOF_0'
diff --git a/Projects/Server/Items/BaseMulti.cs b/Projects/Server/Items/BaseMulti.cs
index 0ebcac7c5..d5f408766 100644
--- a/Projects/Server/Items/BaseMulti.cs
+++ b/Projects/Server/Items/BaseMulti.cs
@@ -1,4 +1,4 @@
-/*************************************************************************
+﻿/*************************************************************************
  * ModernUO                                                              *
  * Copyright 2019-2026 - ModernUO Development Team                       *
  * Email: hi@modernuo.com                                                *
@@ -108,9 +108,11 @@ public override void OnMapChange()
         PathInteriorCacheState = MultiInteriorCacheState.Unknown;
     }
 
-    public override int GetMaxUpdateRange() => 22;
+    // GUO patch: was a literal 22 -- a house has to arrive before its edge
+    // does, so a multi reaches four tiles further than everything else.
+    public override int GetMaxUpdateRange() => Core.GlobalUpdateRange + 4;
 
-    public override int GetUpdateRange(Mobile m) => 22;
+    public override int GetUpdateRange(Mobile m) => Core.GlobalUpdateRange + 4;
 
     public virtual bool Contains(Point2D p) => Contains(p.m_X, p.m_Y);
 
diff --git a/Projects/Server/Items/Item.cs b/Projects/Server/Items/Item.cs
index 03ff9d000..97cc77d74 100644
--- a/Projects/Server/Items/Item.cs
+++ b/Projects/Server/Items/Item.cs
@@ -1,4 +1,4 @@
-/*************************************************************************
+﻿/*************************************************************************
  * ModernUO                                                              *
  * Copyright 2019-2026 - ModernUO Development Team                       *
  * Email: hi@modernuo.com                                                *
@@ -3301,9 +3301,12 @@ private void FixHolding_Sandbox()
         }
     }
 
-    public virtual int GetMaxUpdateRange() => 18;
+    // GUO patch: was a literal 18. Core.GlobalUpdateRange is the same
+    // number and can be raised for a client whose window is wider than the
+    // range -- see the note on it.
+    public virtual int GetMaxUpdateRange() => Core.GlobalUpdateRange;
 
-    public virtual int GetUpdateRange(Mobile m) => 18;
+    public virtual int GetUpdateRange(Mobile m) => Core.GlobalUpdateRange;
 
     public virtual void SendInfoTo(NetState ns, ReadOnlySpan<byte> world = default)
     {
diff --git a/Projects/Server/Main.cs b/Projects/Server/Main.cs
index 608256651..3d74add91 100644
--- a/Projects/Server/Main.cs
+++ b/Projects/Server/Main.cs
@@ -313,9 +313,33 @@ public static string BaseDirectory
 
     public static bool Headless { get; private set; }
 
-    public static int GlobalUpdateRange { get; set; } = 18;
+    // GUO patch: the update range is how far from a player the server
+    // bothers to tell them about items and mobiles. UO's 18 tiles was chosen
+    // for a 640x480 client, where it is a little more than the screen. A
+    // modern client filling a 4K monitor draws map art some 70 tiles out, so
+    // everything the server owns -- doors, signs, decoration, NPCs -- stops
+    // dead in a circle around the player while the terrain carries on. Set
+    // UO_SHARD_UPDATE_RANGE to cover the window and the circle goes away, at
+    // the cost of sending far more to each client.
+    //
+    // Unset, this is 18/24 and the shard behaves exactly as a production one.
+    private static int ReadRangeFromEnvironment(int fallback)
+    {
+        var raw = Environment.GetEnvironmentVariable("UO_SHARD_UPDATE_RANGE");
+
+        if (!int.TryParse(raw, out var range) || range < 5 || range > 200)
+        {
+            return fallback;
+        }
+
+        // 18 and 24 upstream: the outer one is the sweep radius and has to
+        // reach past the inner one, so the six tiles between them are kept.
+        return fallback == 18 ? range : range + 6;
+    }
+
+    public static int GlobalUpdateRange { get; set; } = ReadRangeFromEnvironment(18);
 
-    public static int GlobalMaxUpdateRange { get; set; } = 24;
+    public static int GlobalMaxUpdateRange { get; set; } = ReadRangeFromEnvironment(24);
 
     public static int ScriptItems => _itemCount;
     public static int ScriptMobiles => _mobileCount;
diff --git a/Projects/UOContent/Network/Packets/IncomingPlayerPackets.cs b/Projects/UOContent/Network/Packets/IncomingPlayerPackets.cs
index 384e468b9..af68eb087 100644
--- a/Projects/UOContent/Network/Packets/IncomingPlayerPackets.cs
+++ b/Projects/UOContent/Network/Packets/IncomingPlayerPackets.cs
@@ -1,4 +1,4 @@
-/*************************************************************************
+﻿/*************************************************************************
  * ModernUO                                                              *
  * Copyright 2019-2026 - ModernUO Development Team                       *
  * Email: hi@modernuo.com                                                *
@@ -383,7 +383,10 @@ public static void PingReq(NetState state, SpanReader reader)
 
     public static void SetUpdateRange(NetState state, SpanReader reader)
     {
-        state.SendChangeUpdateRange(18);
+        // GUO patch: was a literal 18. The client asks for a range and is
+        // told the server's, which it then uses to decide when to forget an
+        // object -- so the two numbers have to be the same one.
+        state.SendChangeUpdateRange((byte)Core.GlobalUpdateRange);
     }
 
     public static void MobileQuery(NetState state, SpanReader reader)
MUO_EOF_0
chmod 0644 $BASE/patches/0002-settable-update-range.patch
install -d "$(dirname $BASE/patches/0003-felucca-spring.patch)"
cat > $BASE/patches/0003-felucca-spring.patch <<'MUO_EOF_0'
diff --git a/Distribution/Data/map-definitions.json b/Distribution/Data/map-definitions.json
index fa7a39e52..245e65bb7 100644
--- a/Distribution/Data/map-definitions.json
+++ b/Distribution/Data/map-definitions.json
@@ -6,7 +6,7 @@
     "name": "Felucca",
     "width": 7168,
     "height": 4096,
-    "season": 4,
+    "season": 0,
     "rules": "FeluccaRules"
   },
   {
MUO_EOF_0
chmod 0644 $BASE/patches/0003-felucca-spring.patch

# checkout at the pin; patches off before the pin moves, on after
if [ ! -d "$SRC/.git" ]; then as_user git clone --no-checkout "$CLONE_URL" "$SRC"; fi
as_user git -C "$SRC" cat-file -e "$PIN^{commit}" 2>/dev/null || as_user git -C "$SRC" fetch origin
# global.json is rewritten below for the installed SDK; put it back before anything else looks at the tree
as_user git -C "$SRC" update-index --no-skip-worktree global.json 2>/dev/null || true
as_user git -C "$SRC" checkout -q -- global.json 2>/dev/null || true
head="$(as_user git -C "$SRC" rev-parse -q --verify HEAD 2>/dev/null || true)"
if [ "$head" != "$PIN" ]; then
    if as_user git -C "$SRC" apply -R --check "$BASE/patches/0003-felucca-spring.patch" >/dev/null 2>&1; then as_user git -C "$SRC" apply -R "$BASE/patches/0003-felucca-spring.patch"; fi
    if as_user git -C "$SRC" apply -R --check "$BASE/patches/0002-settable-update-range.patch" >/dev/null 2>&1; then as_user git -C "$SRC" apply -R "$BASE/patches/0002-settable-update-range.patch"; fi
    if as_user git -C "$SRC" apply -R --check "$BASE/patches/0001-headless-owner-account.patch" >/dev/null 2>&1; then as_user git -C "$SRC" apply -R "$BASE/patches/0001-headless-owner-account.patch"; fi
    as_user git -C "$SRC" checkout -q --detach "$PIN"
fi
if as_user git -C "$SRC" apply --check "$BASE/patches/0001-headless-owner-account.patch" >/dev/null 2>&1; then
    as_user git -C "$SRC" apply "$BASE/patches/0001-headless-owner-account.patch"
elif as_user git -C "$SRC" apply -R --check "$BASE/patches/0001-headless-owner-account.patch" >/dev/null 2>&1; then
    echo "muo_shard: 0001-headless-owner-account.patch already applied, skipping"
else
    as_user git -C "$SRC" apply --check "$BASE/patches/0001-headless-owner-account.patch" || true
    die "0001-headless-owner-account.patch does not apply to the checkout at $PIN; stopping"
fi
if as_user git -C "$SRC" apply --check "$BASE/patches/0002-settable-update-range.patch" >/dev/null 2>&1; then
    as_user git -C "$SRC" apply "$BASE/patches/0002-settable-update-range.patch"
elif as_user git -C "$SRC" apply -R --check "$BASE/patches/0002-settable-update-range.patch" >/dev/null 2>&1; then
    echo "muo_shard: 0002-settable-update-range.patch already applied, skipping"
else
    as_user git -C "$SRC" apply --check "$BASE/patches/0002-settable-update-range.patch" || true
    die "0002-settable-update-range.patch does not apply to the checkout at $PIN; stopping"
fi
if as_user git -C "$SRC" apply --check "$BASE/patches/0003-felucca-spring.patch" >/dev/null 2>&1; then
    as_user git -C "$SRC" apply "$BASE/patches/0003-felucca-spring.patch"
elif as_user git -C "$SRC" apply -R --check "$BASE/patches/0003-felucca-spring.patch" >/dev/null 2>&1; then
    echo "muo_shard: 0003-felucca-spring.patch already applied, skipping"
else
    as_user git -C "$SRC" apply --check "$BASE/patches/0003-felucca-spring.patch" || true
    die "0003-felucca-spring.patch does not apply to the checkout at $PIN; stopping"
fi

# the archive's SDK can trail the SDK version the pin's global.json names; build with the installed one
if [ -f "$SRC/global.json" ]; then
    sdk="$(as_user sh -c 'cd / && dotnet --version')"
    pinned="$(sed -n -E 's/.*"version": *"([^"]*)".*/\1/p' "$SRC/global.json" | head -n 1)"
    if [ "$pinned" != "$sdk" ]; then echo "muo_shard: warning: global.json names SDK $pinned, building with the installed SDK $sdk" >&2; fi
    as_user sed -i -E "s/\"version\": *\"[^\"]*\"/\"version\": \"$sdk\"/" "$SRC/global.json"
    as_user git -C "$SRC" update-index --skip-worktree global.json
fi

# build
arch=x64; [ "$(uname -m)" = aarch64 ] && arch=arm64
as_user sh -c 'cd "$1" && exec bash ./publish.sh release linux "$2"' sh "$SRC" "$arch"
OUT="$SRC/Distribution"
[ -x "$OUT/ModernUO" ] || die "the build left no ModernUO in $OUT"
as_user cp -a "$OUT/." "$DIST/"

# configuration: the template, the overlay over it (last wins), then the host values
install -d "$(dirname $DIST/Configuration/modernuo.json)"
cat > $DIST/Configuration/modernuo.json <<'MUO_EOF_0'
{
  "assemblyDirectories": [
    "./Assemblies"
  ],
  "dataDirectories": [
    ""
  ],
  "listeners": [
    "0.0.0.0:2593"
  ],
  "settings": {
    "accountHandler.enableAutoAccountCreation": "True",
    "accountHandler.enablePlayerPasswordCommand": "False",
    "accountHandler.maxAccountsPerIP": "16",
    "accountSecurity.encryptionAlgorithm": "Argon2",
    "assistants.enableNegotiation": "False",
    "autoArchive.archiveLocally": "True",
    "autoArchive.archivePath": "Archives",
    "autoArchive.backupMaxAge": "30",
    "autoArchive.backupPath": "Backups",
    "autoArchive.compressionLevel": "3",
    "autoArchive.dailyRetention": "30",
    "autoArchive.enableArchivePruning": "True",
    "autoArchive.hourlyRetention": "24",
    "autoArchive.monthlyRetention": "12",
    "autoArchive.retryCount": "3",
    "autoArchive.retryDelayMs": "500",
    "autoArchive.verifyArchives": "True",
    "autosave.enabled": "True",
    "autosave.saveDelay": "00:05:00",
    "autosave.warningDelay": "00:00:00",
    "buffIcons.enable": "True",
    "bulletinboards.creationTimeDelay": "00:02:00",
    "bulletinboards.expireDuration": "06:00:00",
    "bulletinboards.replyDelay": "00:00:30",
    "chat.enabled": "False",
    "clientVerification.ageLeniency": "10.00:00:00",
    "clientVerification.enable": "True",
    "clientVerification.gameTimeLeniency": "1.01:00:00",
    "clientVerification.invalidClientResponse": "Kick",
    "clientVerification.kickDelay": "00:00:20",
    "commandsystem.prefix": "[",
    "crashGuard.enabled": "True",
    "crashGuard.generateReport": "True",
    "crashGuard.restartServer": "True",
    "crashGuard.saveBackup": "True",
    "ethics.enable": "False",
    "houseDecay.enable": "True",
    "movement.delay.runFoot": "200",
    "movement.delay.runMount": "100",
    "movement.delay.turn": "0",
    "movement.delay.walkFoot": "400",
    "movement.delay.walkMount": "200",
    "movementThrottle.definiteRateThreshold": "1.100000023841858",
    "movementThrottle.hardQueueLimit": "10",
    "movementThrottle.maxChainGap": "2000",
    "movementThrottle.maxCredit": "200",
    "movementThrottle.maxRttBonus": "150",
    "movementThrottle.minSamplesForRate": "8",
    "movementThrottle.movementHistorySize": "20",
    "movementThrottle.speedHackNotificationCooldown": "300000",
    "movementThrottle.suspiciousRateThreshold": "1.0499999523162842",
    "murderSystem.bountiesEnabled": "False",
    "murderSystem.bountyExpiry": "14.00:00:00",
    "murderSystem.longTermMurderDuration": "1.16:00:00",
    "murderSystem.recentlyReportedDelay": "00:10:00",
    "murderSystem.shortTermMurderDuration": "08:00:00",
    "network.initialBufferSlabs": "1",
    "network.maxBufferSlabs": "128",
    "network.maxOutstandingSends": "32",
    "network.memoryCeilingPercent": "80",
    "network.sendBufferGrowthBudget": "268435456",
    "network.sendBufferMaxSize": "2097152",
    "network.sendBufferSize": "262144",
    "pages.discordWebhookUrl": null,
    "pathfinding.enable": "True",
    "pathfinding.maxResidentChunks": "8192",
    "pathfinding.maxSearchNodes": "1000",
    "pathfinding.recorder.enable": "False",
    "pathfinding.recorder.path": null,
    "pingServer.enabled": "True",
    "questSystem.enableMLQuests": "True",
    "serverListing.address": null,
    "serverListing.autoDetect": "True",
    "serverListing.serverName": "GUO Dev",
    "stamina.additionalLossWhenBelow": "0.1",
    "stamina.baseOverweightLoss": "5",
    "stamina.cannotRunWhenFatigued": "False",
    "stamina.cannotWalkWhenFatigued": "False",
    "stamina.enableMountStamina": "True",
    "stamina.stonesOverweightAllowance": "4",
    "stamina.stonesPerOverweightLoss": "25",
    "stats.gainChanceMultiplier": "1",
    "stats.statMax": "125",
    "testCenter.enable": "False",
    "timer.initialPoolCapacity": "1024",
    "timer.maxPoolCapacity": "16384",
    "uogateway.enabled": "True",
    "vetRewards.enable": "True",
    "vetRewards.rewardInterval": "30.00:00:00",
    "vetRewards.skillCapRewards": "True",
    "world.enableAutoRestart": "False",
    "world.savePath": "Saves",
    "world.useMultithreadedSaves": "True"
  }
}
MUO_EOF_0
chmod 0644 $DIST/Configuration/modernuo.json
install -d "$(dirname $DIST/Configuration/expansion.json)"
cat > $DIST/Configuration/expansion.json <<'MUO_EOF_0'
{
  "Id": 11,
  "Name": "Endless Journey",
  "RequiredClient": "7.0.61.0",
  "ClientFlags": "None",
  "SupportedFeatures": {
    "T2A": true,
    "UOR": true,
    "UOTD": false,
    "LBR": true,
    "AOS": true,
    "SixthCharacterSlot": false,
    "SE": true,
    "ML": true,
    "EighthAge": false,
    "NinthAge": true,
    "TenthAge": false,
    "IncreasedStorage": false,
    "SeventhCharacterSlot": false,
    "RoleplayFaces": false,
    "TrialAccount": false,
    "LiveAccount": true,
    "SA": true,
    "HS": true,
    "Gothic": true,
    "Rustic": true,
    "Jungle": true,
    "Shadowguard": true,
    "TOL": true,
    "EJ": true
  },
  "MapSelectionFlags": {
    "Felucca": true,
    "Trammel": true,
    "Ilshenar": true,
    "Malas": true,
    "Tokuno": true,
    "TerMer": true
  },
  "CharacterListFlags": {
    "Unk1": false,
    "OverwriteConfigButton": false,
    "OneCharacterSlot": false,
    "ContextMenus": true,
    "SlotLimit": false,
    "AOS": true,
    "SixthCharacterSlot": false,
    "SE": true,
    "ML": true,
    "Unk2": false,
    "UO3DClientType": false,
    "Unk3": false,
    "SeventhCharacterSlot": false,
    "Unk4": false,
    "NewMovementSystem": false,
    "NewFeluccaAreas": false
  },
  "HousingFlags": {
    "AOS": true,
    "SE": true,
    "ML": true,
    "Crystal": true,
    "SA": true,
    "HS": true,
    "Gothic": true,
    "Rustic": true,
    "Jungle": true,
    "Shadowguard": true,
    "TOL": true,
    "EJ": true
  },
  "MobileStatusVersion": 6
}
MUO_EOF_0
chmod 0644 $DIST/Configuration/expansion.json
install -d "$(dirname $BASE/muo-fill.py)"
cat > $BASE/muo-fill.py <<'MUO_EOF_0'
import os, sys
root = sys.argv[1]
for rel in sys.argv[2:]:
    path = os.path.join(root, rel)
    text = open(path, encoding="utf-8").read()
    out, rest = [], text
    while "{{" in rest:
        head, _, tail = rest.partition("{{")
        key, sep, rest = tail.partition("}}")
        if not sep or not key.isidentifier() or key not in os.environ:
            sys.exit("muo_shard: " + rel + " uses {{" + key + "}}, which is not set in the host env file")
        out += [head, os.environ[key]]
    out.append(rest)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(out))
MUO_EOF_0
chmod 0644 $BASE/muo-fill.py
install -d "$(dirname $BASE/muo-finish-config.py)"
cat > $BASE/muo-finish-config.py <<'MUO_EOF_0'
import json, sys
path, data, version = sys.argv[1:4]
doc = json.load(open(path, encoding="utf-8"))
doc["dataDirectories"] = [data]
if version:
    doc.setdefault("settings", {})["clientData.clientVersion"] = version
with open(path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
MUO_EOF_0
chmod 0644 $BASE/muo-finish-config.py
python3 "$BASE/muo-fill.py" "$DIST/Configuration" expansion.json
python3 "$BASE/muo-finish-config.py" "$DIST/Configuration/modernuo.json" "$MUO_CLIENT_DATA" ''

# start script: the unit's one ExecStart; maps the host keys to the names the shard reads
install -d "$(dirname $DIST/muo-run.sh)"
cat > $DIST/muo-run.sh <<'MUO_EOF_0'
#!/bin/sh
# generated by muo_shard
# the dev-only GM account list in the patch is never honoured by a deployed shard
unset UO_SHARD_GM_ACCOUNTS
if [ -n "${MUO_ADMIN_USER:-}" ]; then export UO_SHARD_OWNER="$MUO_ADMIN_USER"; fi
if [ -n "${MUO_ADMIN_PASSWORD:-}" ]; then export UO_SHARD_OWNER_PASSWORD="$MUO_ADMIN_PASSWORD"; fi
cd "$(dirname "$0")"
exec ./ModernUO
MUO_EOF_0
chmod 0755 $DIST/muo-run.sh

# ownership; Saves/ and Logs/ are never touched
chown -R "$SVC_USER:$SVC_USER" "$DIST"

# the service
install -d "$(dirname $BASE/unit.new)"
cat > $BASE/unit.new <<'MUO_EOF_0'
[Unit]
Description=ModernUO shard GUO Dev (muo-guo-dev.service)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=muo-guo-dev
Group=muo-guo-dev
WorkingDirectory=/srv/muo/guo-dev/dist
EnvironmentFile=/etc/muo/guo-dev.env
ExecStart=/srv/muo/guo-dev/dist/muo-run.sh
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
MemoryMax=4G

[Install]
WantedBy=multi-user.target
MUO_EOF_0
chmod 0644 $BASE/unit.new
if have_systemd; then
    if ! cmp -s "$BASE/unit.new" "/etc/systemd/system/$UNIT"; then
        install -m 0644 "$BASE/unit.new" "/etc/systemd/system/$UNIT"
        systemctl daemon-reload
    fi
    systemctl enable "$UNIT"
    systemctl restart "$UNIT"
else
    echo "muo_shard: no systemd here (a container?). Start the shard with:"
    echo "  set -a; . $ENV_FILE; set +a; runuser -u $SVC_USER -- $DIST/muo-run.sh"
fi
rm -f "$BASE/unit.new"
echo "muo_shard: deploy of $ID done at ${PIN:0:9}; check with: run.py plan status"
