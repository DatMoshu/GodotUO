// Checks for the bridge's admin channel (ADR-0035): the token, the access
// levels, and that no password or token reaches the audit log.
using System.Text.Json.Nodes;
using GUO.EditorBridge;
using GUO.Workspace;

void Require(bool ok, string what) { if (!ok) throw new Exception(what); }

string home = Path.Combine(Path.GetTempPath(), "guo-admin-" + Guid.NewGuid().ToString("N"));
string auditPath = Path.Combine(home, "Logs", "GUO", "admin_audit.jsonl");
const string token = "Tk7qL2vX9pR4mN8sW3yB6cD1fG5hJ0aZ";
try
{
    // ---- the token -------------------------------------------------------
    var off = new AdminChannel(null, AdminLevel.Administrator, new AuditLog(null));
    Require(!off.Enabled, "a server without a token has an admin channel");
    Require(off.CheckHello(null, out string r0) == null && r0 == null, "a plain hello was refused");
    Require(off.CheckHello(token, out string r1) == null && r1 == "this server has no admin token", "a token was accepted by a server with none");
    Require(off.Authorise("admin_whoami", AdminLevel.Owner) == "this server has no admin token", "an admin op ran on a server with no token");

    var audit = new AuditLog(auditPath);
    var on = new AdminChannel(token, AdminLevel.Administrator, audit);
    Require(on.Enabled, "a token did not enable the channel");
    Require(on.CheckHello(null, out string r2) == null && r2 == null, "a plain hello (map editing) was refused");
    Require(on.CheckHello("", out string r3) == null && r3 == null, "an empty token counted as a refusal");
    Require(on.CheckHello(token + "x", out string r4) == null && r4 == "admin token refused", "a longer token was accepted");
    Require(on.CheckHello(token[..^1], out _) == null, "a prefix of the token was accepted");
    Require(on.CheckHello(token.ToLowerInvariant(), out _) == null, "the token compared case-insensitively");
    Require(on.CheckHello(token, out string r5) == AdminLevel.Administrator && r5 == null, "the right token was refused");
    Console.WriteLine("PASS: token required, wrong/prefix/longer/case-changed tokens refused, a plain hello is not a refusal");

    // ---- access levels ---------------------------------------------------
    Require(AdminChannel.IsAdminOp("admin_whoami") && AdminChannel.IsAdminOp("admin_audit"), "admin ops not listed");
    foreach (string mapOp in new[] { "hello", "block", "object", "multi", "mobiles", "equip", null })
    {
        Require(!AdminChannel.IsAdminOp(mapOp), $"'{mapOp}' became an admin op (map editing must stay as it was)");
    }

    Require(on.Authorise("admin_whoami", null).StartsWith("admin op without the admin token"), "an admin op ran without the token");
    Require(on.Authorise("admin_whoami", AdminLevel.Counselor) == null, "a Counselor could not run whoami");
    Require(on.Authorise("admin_audit", AdminLevel.GameMaster) == "'admin_audit' needs Administrator; this connection holds GameMaster",
            "a GameMaster could read the audit log");
    Require(on.Authorise("admin_audit", AdminLevel.Administrator) == null, "an Administrator could not read the audit log");
    Require(on.Authorise("block", AdminLevel.Owner) == "'block' is not an admin op", "a non-admin op was authorised as one");
    var gm = new AdminChannel(token, AdminLevel.GameMaster, new AuditLog(null));
    Require(gm.CheckHello(token, out _) == AdminLevel.GameMaster, "the granted level is not the configured one");
    Require(gm.OpsFor(AdminLevel.GameMaster).ToJsonString() == "[\"admin_whoami\",\"admin_status\",\"admin_godview\",\"admin_godview_find\",\"admin_goto\",\"admin_bring\",\"admin_paperdoll\",\"admin_follow\",\"admin_spawner\",\"admin_commands\",\"admin_command\",\"command\"]", "OpsFor lists ops above the level");
    // AD1: Health is read-only (a Counselor may look); a save is an Administrator's, as ModernUO's own [save.
    Require(AdminChannel.Ops["admin_status"] == AdminLevel.Counselor && AdminChannel.Ops["admin_save"] == AdminLevel.Administrator,
            "the Health ops' levels changed");
    Require(on.Authorise("admin_save", AdminLevel.GameMaster) == "'admin_save' needs Administrator; this connection holds GameMaster",
            "a GameMaster could save the world");
    Require(on.Authorise("admin_status", null).StartsWith("admin op without the admin token"), "Health ran without the token");
    Require(on.OpsFor(AdminLevel.Administrator).Count == AdminChannel.Ops.Count, "OpsFor misses ops at the level");
    Console.WriteLine("PASS: every admin op has a level, the token's level bounds what runs, map-editing ops are not admin ops");

    // ---- the audit log never holds a secret -------------------------------
    var hello = JsonNode.Parse($"{{\"op\":\"hello\",\"editor\":\"Moshu-editor\",\"admin_token\":\"{token}\"}}");
    audit.Record("Moshu-editor", "hello", AdminLevel.Administrator, true, hello);
    var reset = JsonNode.Parse("{\"op\":\"admin_account_password\",\"account\":\"guoprobe\",\"password\":\"Secret123Pass\","
                               + "\"options\":{\"newPassword\":\"Other456Pass\",\"keep\":[{\"api_secret\":\"S3cr3tValue\"}]}}");
    audit.Record("Moshu-editor", "admin_account_password", AdminLevel.Administrator, false, reset, "not an admin op yet");
    audit.Record("Moshu-editor", "admin_whoami", AdminLevel.Counselor, true, JsonNode.Parse("{\"op\":\"admin_whoami\",\"req\":4}"));
    string written = File.ReadAllText(auditPath);
    string recent = audit.Recent(10).ToJsonString();
    foreach (string secret in new[] { token, "Secret123Pass", "Other456Pass", "S3cr3tValue" })
    {
        Require(!written.Contains(secret), $"the audit file holds a secret ({secret[..2]}...)");
        Require(!recent.Contains(secret), $"the in-memory audit holds a secret ({secret[..2]}...)");
    }

    string[] lines = File.ReadAllLines(auditPath);
    Require(lines.Length == 3, $"expected 3 audit lines, got {lines.Length}");
    var first = JsonNode.Parse(lines[0])!;
    Require((string)first["editor"] == "Moshu-editor" && (string)first["op"] == "hello" && (bool)first["ok"]
            && (string)first["level"] == "Administrator" && ((string)first["at"]).EndsWith("Z"), "an audit entry lacks who/what/when");
    Require((string)first["args"]["admin_token"] == "***", "the token field was not masked");
    var second = JsonNode.Parse(lines[1])!;
    Require((string)second["args"]["account"] == "guoprobe" && (string)second["error"] == "not an admin op yet", "a plain field or the refusal was lost");
    Require((string)second["args"]["options"]["keep"][0]["api_secret"] == "***", "a nested secret was not masked");
    Require((int)JsonNode.Parse(lines[2])!["args"]["req"] == 4, "a plain field was masked");
    Console.WriteLine("PASS: audit entries say who, what, when and the outcome; tokens and passwords are masked, nested too");

    // ---- the in-memory log is bounded ------------------------------------
    var small = new AuditLog(null);
    for (int i = 0; i < AuditLog.Keep + 25; i++)
    {
        small.Record("e", "admin_whoami", AdminLevel.Counselor, true, JsonNode.Parse($"{{\"req\":{i}}}"));
    }

    JsonArray last = small.Recent(1000);
    Require(last.Count == AuditLog.Keep, "the in-memory audit is not capped");
    Require((int)last[^1]!["args"]["req"] == AuditLog.Keep + 24 && (int)small.Recent(2)[0]!["args"]["req"] == AuditLog.Keep + 23,
            "Recent does not return the newest entries, oldest first");
    Console.WriteLine("PASS: the in-memory audit keeps the newest entries, oldest first");
    // ---- AD2a: the god view's levels, change-only pushes, caps and Find ------
    Require(AdminChannel.Ops["admin_godview"] == AdminLevel.GameMaster && AdminChannel.Ops["admin_godview_find"] == AdminLevel.GameMaster,
            "the god view's levels changed");
    Require(on.Authorise("admin_godview", AdminLevel.Counselor) == "'admin_godview' needs GameMaster; this connection holds Counselor",
            "a Counselor could watch the whole facet");
    Require(on.Authorise("admin_godview", null).StartsWith("admin op without the admin token"), "the god view ran without the token");

    // Entries carry a hash of their row; the diff builds JSON only for those that changed.
    int built = 0;
    JsonObject Build(object o)
    {
        built++;
        var (serial, x) = ((uint, int))o;
        return new JsonObject { ["serial"] = serial, ["kind"] = "npc", ["x"] = x };
    }

    static GodViewEntry E(uint serial, int x) => new(serial, HashCode.Combine(x), (serial, x));
    var diff = new GodViewDiff();
    var (up1, rm1) = diff.Next(new List<GodViewEntry> { E(1, 5), E(2, 6), E(3, 7) }, Build);
    Require(up1.Count == 3 && rm1.Count == 0 && diff.Seq == 1 && diff.Held == 3 && built == 3, "the first push is not the whole list");
    var (up2, rm2) = diff.Next(new List<GodViewEntry> { E(1, 5), E(2, 6), E(3, 7) }, Build);
    Require(up2.Count == 0 && rm2.Count == 0 && diff.Seq == 1 && built == 3, "an unchanged facet sent (or built) rows");
    var (up3, rm3) = diff.Next(new List<GodViewEntry> { E(1, 5), E(2, 9), E(4, 1) }, Build);
    Require(up3.Count == 2 && (uint)up3[0]["serial"] == 2 && (int)up3[0]["x"] == 9 && (uint)up3[1]["serial"] == 4 && built == 5,
            "a move or a newcomer was not sent, or an unchanged row was built");
    Require(rm3.ToJsonString() == "[3]" && diff.Seq == 2 && diff.Held == 3, "a mobile that left was not removed");
    var (up4, rm4) = diff.Next(new List<GodViewEntry> { E(1, 5), E(1, 5), E(2, 9), E(4, 1) }, Build);
    Require(up4.Count == 0 && rm4.Count == 0, "a duplicate serial counted as a change");
    diff.Reset();
    Require(diff.Next(new List<GodViewEntry> { E(1, 5) }, Build).Upsert.Count == 1 && diff.Seq == 1, "Reset did not send the whole list again");

    var many = new List<GodViewEntry>();
    for (uint i = 20; i > 0; i--)
    {
        many.Add(E(i, (int)i));
    }

    List<GodViewEntry> capped = GodViewDiff.Cap(many, 5, out bool cut);
    Require(cut && capped.Count == 5 && capped.Select(r => r.Serial).SequenceEqual(new uint[] { 1, 2, 3, 4, 5 }),
            "the cap does not keep the same (lowest-serial) rows each push");
    Require(GodViewDiff.Cap(new List<GodViewEntry> { E(1, 1) }, 5, out bool notCut).Count == 1 && !notCut, "a list under the cap was cut");

    Require(GodViewDiff.Matches("ORC", 7, "an orc captain") && GodViewDiff.Matches("orc", 7, null, "OrcishMage"), "Find misses a name");
    Require(!GodViewDiff.Matches("orc", 7, "a troll") && !GodViewDiff.Matches("  ", 7, "orc"), "Find matched what it should not");
    Require(GodViewDiff.Matches("0x1A", 26, "x") && GodViewDiff.Matches("26", 26, "x") && !GodViewDiff.Matches("0x1A", 27, "0x1A"),
            "Find by serial (hex or decimal) is wrong");
    Require(GodViewDiff.ParseSerial("0xZZ") == null && GodViewDiff.ParseSerial("12a") == null && GodViewDiff.ParseSerial("4294967295") == uint.MaxValue,
            "serial parsing is wrong");
    Console.WriteLine("PASS: the god view needs GameMaster and the token; pushes carry only changes; caps keep the same rows; Find by name or serial");

    // ---- AD2b: the god view's actions ----------------------------------------
    foreach (string op in new[] { "admin_goto", "admin_bring", "admin_paperdoll", "admin_follow", "admin_spawner" })
    {
        Require(AdminChannel.Ops[op] == AdminLevel.GameMaster, $"'{op}' is not a GameMaster op");
        Require(on.Authorise(op, null).StartsWith("admin op without the admin token"), $"'{op}' ran without the token");
        Require(on.Authorise(op, AdminLevel.Counselor) == $"'{op}' needs GameMaster; this connection holds Counselor", $"a Counselor could run '{op}'");
    }

    // Which target each action takes.
    Require(GodViewRules.CheckTarget("admin_goto", "spawner", false) == null && GodViewRules.CheckTarget("admin_goto", "item", false) == null
            && GodViewRules.CheckTarget("admin_goto", "npc", false) == null, "Go there refuses a place it should go to");
    Require(GodViewRules.CheckTarget("admin_goto", "player", true) == "that is your own character", "Go there to yourself was allowed");
    Require(GodViewRules.CheckTarget("admin_bring", "player", false) == null && GodViewRules.CheckTarget("admin_bring", "npc", false) == null,
            "Bring here refuses a mobile");
    Require(GodViewRules.CheckTarget("admin_bring", "spawner", false) == "only a player or an NPC can be brought", "a spawner could be brought");
    Require(GodViewRules.CheckTarget("admin_bring", "player", true) == "that is your own character", "you could bring yourself");
    Require(GodViewRules.CheckTarget("admin_paperdoll", "npc", false) == null && GodViewRules.CheckTarget("admin_paperdoll", "player", true) == null,
            "a paperdoll was refused (your own too may be opened)");
    Require(GodViewRules.CheckTarget("admin_paperdoll", "spawner", false) == "only a player or an NPC has a paperdoll", "a spawner's paperdoll opened");
    Require(GodViewRules.CheckTarget("admin_follow", "npc", false) == null && GodViewRules.CheckTarget("admin_follow", "player", true) != null
            && GodViewRules.CheckTarget("admin_follow", "item", false) == "only a player or an NPC can be followed", "Follow's targets are wrong");
    Require(GodViewRules.CheckTarget("admin_spawner", "spawner", false) == null && GodViewRules.CheckTarget("admin_spawner", "npc", false) == "that is not a spawner",
            "spawner actions took a non-spawner");
    foreach (string op in new[] { "admin_goto", "admin_bring", "admin_paperdoll", "admin_follow", "admin_spawner" })
    {
        Require(GodViewRules.CheckTarget(op, null, false) == "nothing in the world has that serial", $"'{op}' took a serial that is not there");
    }

    Require(GodViewRules.CheckTarget("admin_godview", "npc", false) == "'admin_godview' is not a god view action", "a non-action passed as one");

    // Only the admin's own online staff character is moved.
    Require(GodViewRules.CheckCharacter("Moshu", true, AdminLevel.GameMaster) == null && GodViewRules.CheckCharacter("Moshu", true, AdminLevel.Owner) == null,
            "a staff character was refused");
    Require(GodViewRules.CheckCharacter("Bob", true, AdminLevel.Player) == "'Bob' is Player; the god view moves only your own staff character (GameMaster or higher)",
            "a player character could be moved by the god view");
    Require(GodViewRules.CheckCharacter("Bob", true, AdminLevel.Counselor) != null, "a Counselor character could be moved");
    Require(GodViewRules.CheckCharacter("Moshu", false, AdminLevel.Owner) == "'Moshu' is not online: log in with your staff character first",
            "an offline character was accepted");

    // Follow moves only when the target is on another facet or further than FollowRange.
    Require(!GodViewRules.FollowMustMove(0, 100, 100, 0, 102, 98) && !GodViewRules.FollowMustMove(0, 100, 100, 0, 100, 100), "Follow moved within range");
    Require(GodViewRules.FollowMustMove(0, 100, 100, 0, 103, 100) && GodViewRules.FollowMustMove(0, 100, 100, 0, 100, 97), "Follow stayed out of range");
    Require(GodViewRules.FollowMustMove(0, 100, 100, 1, 100, 100) && GodViewRules.FollowMustMove(-1, 0, 0, 0, 0, 0), "Follow stayed on another facet");
    Console.WriteLine("PASS: the actions need GameMaster and the token; each takes only its kind of target; only an online staff character moves; Follow keeps within 2 tiles");

    // ---- AD4: the Settings form -------------------------------------------------
    Require(AdminChannel.Ops["admin_settings"] == AdminLevel.Administrator, "admin_settings is not an Administrator op");
    Require(on.Authorise("admin_settings", AdminLevel.GameMaster) == "'admin_settings' needs Administrator; this connection holds GameMaster",
            "a GameMaster could change the settings");
    Require(on.Authorise("admin_settings", null).StartsWith("admin op without the admin token"), "settings ran without the token");
    // The form's diff names each setting by "key": a secret setting's values are masked by its name, a plain one's kept.
    const string hook = "https://example.invalid/hook/Hook3Secret";
    var settingsAudit = new AuditLog(Path.Combine(home, "settings_audit.jsonl"));
    settingsAudit.Record("Admin tab", "admin_settings", AdminLevel.Administrator, true, JsonNode.Parse(
        "{\"op\":\"admin_settings\",\"action\":\"changed\",\"changes\":[{\"file\":\"Configuration/modernuo.json\",\"key\":\"settings/pages.discordWebhookUrl\",\"from\":null,\"to\":\"" + hook + "\"},"
        + "{\"file\":\"Configuration/email-settings.json\",\"key\":\"emailPassword\",\"to\":\"Mail9Secret\"},"
        + "{\"file\":\"Configuration/modernuo.json\",\"key\":\"settings/accountHandler.maxAccountsPerIP\",\"from\":\"16\",\"to\":\"15\"}]}"));
    string settingsLine = File.ReadAllText(Path.Combine(home, "settings_audit.jsonl"));
    Require(!settingsLine.Contains("Hook3Secret") && !settingsLine.Contains("Mail9Secret") && settingsLine.Contains("\"to\":\"15\"")
            && settingsLine.Contains("\"key\":\"settings/pages.discordWebhookUrl\""), "a secret setting's value reached the audit, or a plain one was masked");
    Require(AuditLog.IsSecretName("pages.discordWebhookUrl") && !AuditLog.IsSecretName("emailPort"), "a webhook is not a secret name");
    Console.WriteLine("PASS: the Settings op needs Administrator and the token; a secret setting's value never reaches the audit");

    // ---- AD5: accounts -----------------------------------------------------
    Require(AdminChannel.Ops["admin_accounts"] == AdminLevel.Administrator && AdminChannel.Ops["admin_account"] == AdminLevel.Administrator,
            "the account ops are not Administrator (ModernUO's admin gump)");
    Require(on.Authorise("admin_account", AdminLevel.GameMaster) == "'admin_account' needs Administrator; this connection holds GameMaster",
            "a GameMaster could change accounts");
    Require(on.Authorise("admin_accounts", null).StartsWith("admin op without the admin token"), "accounts listed without the token");
    // Names and passwords fit the login gump's 16-character boxes; names follow ModernUO's IsValidUsername.
    Require(AccountRules.CheckName("guoad5") == null && AccountRules.CheckName(new string('a', 16)) == null, "a good name was refused");
    foreach (string bad in new[] { "", new string('a', 17), " lead", "trail ", "dot.", "a<b", "a:b", "a/b", "tab\tname", "caf\u00e9" })
    {
        Require(AccountRules.CheckName(bad) != null, $"a bad name was taken: '{bad}'");
    }

    Require(AccountRules.CheckPassword("guoad5", "Abcdefgh12345678") == null && AccountRules.CheckPassword("guoad5", "x9!y8@z7") == null,
            "a good password was refused");
    Require(AccountRules.CheckPassword("guoad5", "Abcdefgh123456789")!.Contains("16"), "a 17-character password was taken (the login box holds 16)");
    foreach (string bad in new[] { null, "", "short7!", "has space1", "GUOAD5", "guoprobe", "p\u00e4sswort1" })
    {
        Require(AccountRules.CheckPassword("guoad5", bad) != null, $"a bad password was taken ({bad?.Length} chars)");
    }

    // Levels as ModernUO's admin gump: below the connection's own, any for an Owner; nothing at or above it is changed.
    Require(AccountRules.CheckLevel(AdminLevel.Administrator, AdminLevel.Seer) == null, "an Administrator could not make a Seer");
    Require(AccountRules.CheckLevel(AdminLevel.Administrator, AdminLevel.Administrator) != null, "an Administrator could make another Administrator");
    Require(AccountRules.CheckLevel(AdminLevel.Administrator, AdminLevel.Owner) != null, "an Administrator could make an Owner");
    Require(AccountRules.CheckLevel(AdminLevel.Owner, AdminLevel.Owner) == null, "an Owner could not make an Owner");
    Require(AccountRules.CheckTarget(AdminLevel.Administrator, AdminLevel.GameMaster) == null, "an Administrator could not change a GameMaster");
    Require(AccountRules.CheckTarget(AdminLevel.Administrator, AdminLevel.Administrator) != null
            && AccountRules.CheckTarget(AdminLevel.Administrator, AdminLevel.Owner)!.Contains("Owner"), "an Administrator could change an account at or above its level");
    Require(AccountRules.CheckTarget(AdminLevel.Owner, AdminLevel.Owner) == null, "an Owner could not change an Owner account");
    Require(AccountRules.ParseLevel("gamemaster") == AdminLevel.GameMaster && AccountRules.ParseLevel("3") == null
            && AccountRules.ParseLevel("King") == null && AccountRules.ParseLevel(null) == null, "access level names are not parsed as ModernUO's");
    // The password reaches neither audit, in a create or a reset.
    var accountAudit = new AuditLog(Path.Combine(home, "accounts_audit.jsonl"));
    accountAudit.Record("Admin tab", "admin_account", AdminLevel.Administrator, true,
        JsonNode.Parse("{\"op\":\"admin_account\",\"action\":\"create\",\"account\":\"guoad5\",\"password\":\"Ad5Pass9word77\",\"access\":\"Player\"}"));
    accountAudit.Record("Admin tab", "admin_account", AdminLevel.Administrator, false,
        JsonNode.Parse("{\"op\":\"admin_account\",\"action\":\"password\",\"account\":\"guoad5\",\"password\":\"Ad5Other5555\"}"), "refused");
    string accountLines = File.ReadAllText(Path.Combine(home, "accounts_audit.jsonl")) + accountAudit.Recent(5).ToJsonString();
    Require(!accountLines.Contains("Ad5Pass9word77") && !accountLines.Contains("Ad5Other5555") && accountLines.Contains("\"account\":\"guoad5\"")
            && accountLines.Contains("\"password\":\"***\""), "an account password reached the audit, or the account's name was masked");
    Console.WriteLine("PASS: the account ops need Administrator and the token; names and passwords fit the 16-character login boxes; levels as the admin gump; no password in the audit");

    // ---- AD6: backups ------------------------------------------------------
    Require(AdminChannel.Ops["admin_backup"] == AdminLevel.Administrator, "admin_backup is not an Administrator op");
    Require(on.Authorise("admin_backup", AdminLevel.Seer) == "'admin_backup' needs Administrator; this connection holds Seer",
            "a Seer could back up or restore");
    Require(on.Authorise("admin_backup", null).StartsWith("admin op without the admin token"), "a backup ran without the token");
    Console.WriteLine("PASS: the backup op needs Administrator and the token");
    // ---- commands (AD3) ----------------------------------------------------
    foreach (string op in new[] { "admin_commands", "admin_command", "command" })
    {
        Require(AdminChannel.Ops[op] == AdminLevel.Counselor, $"{op} is not a Counselor op");
        Require(on.Authorise(op, null).StartsWith("admin op without the admin token"), $"{op} ran without the token");
        Require(on.Authorise(op, AdminLevel.Player) != null, $"{op} ran for a Player connection");
        Require(on.Authorise(op, AdminLevel.Counselor) == null, $"a Counselor could not run {op}");
    }

    Require(AdminCommandRules.Normalise(" where ") == "[where" && AdminCommandRules.Normalise("[where") == "[where", "Normalise");
    Require(AdminCommandRules.Name("[ Wipe items") == "Wipe", "Name");
    Require(AdminCommandRules.Check("") != null && AdminCommandRules.Check("[") != null, "an empty command was taken");
    Require(AdminCommandRules.Check("[where\n[wipe") =="a command is one line", "a second line was taken");
    Require(AdminCommandRules.Check("[" + new string('a', AdminCommandRules.MaxLength)) != null, "an over-long command was taken");
    Require(AdminCommandRules.Check("[where") == null, "[where was refused");
    foreach (string safe in new[] { "[where", "[go britain", "[add Bandage", "[set hits 50", "[props", "[delete", "[single set hue 5",
                                     "[self delete", "[serial delete", "[global interface", "[area interface", "[decorat", "[save" })
    {
        Require(AdminCommandRules.Classify(safe) == null, $"'{safe}' is on the dangerous list");
    }

    (string Line, string Kind, string Confirm)[] dangerous =
    {
        ("[restart", "shutdown", "restart"), ("[Shutdown", "shutdown", "shutdown"), ("[wipe", "wipe", "wipe"),
        ("[WipeItems", "wipe", "wipeitems"), ("[clearfacet", "wipe", "clearfacet"), ("[deleteaccount bob", "delete accounts", "deleteaccount"),
        ("[decorate", "global decorate", "decorate"), ("[DecorateMag", "global decorate", "decoratemag"), ("[telgen", "global decorate", "telgen"),
        ("[global delete where Item", "mass moves", "global delete"), ("[Area Set hue 5", "mass moves", "area set"),
        ("[online tele", "mass moves", "online tele"), ("[region kill", "mass moves", "region kill"), ("[facet remove", "mass moves", "facet remove"),
    };
    foreach (var (line, kind, confirm) in dangerous)
    {
        AdminCommandRules.Danger d = AdminCommandRules.Classify(line);
        Require(d != null && d.Kind == kind && d.Confirm == confirm, $"'{line}' classified as {d?.Kind}/{d?.Confirm}, want {kind}/{confirm}");
        Require(!string.IsNullOrEmpty(d.Why), $"'{line}' has no reason");
        Require(AdminCommandRules.Confirms(d, confirm.ToUpperInvariant()) && AdminCommandRules.Confirms(d, "  " + confirm.Replace(" ", "   ") + " "),
            $"'{confirm}' did not confirm '{line}'");
        Require(!AdminCommandRules.Confirms(d, null) && !AdminCommandRules.Confirms(d, "") && !AdminCommandRules.Confirms(d, "yes") &&
                !AdminCommandRules.Confirms(d, confirm + "x"), $"a wrong word confirmed '{line}'");
    }

    Require(AdminCommandRules.CheckCharacter("Moshu", false, 4, 4)?.Contains("not online") == true, "an offline character ran commands");
    Require(AdminCommandRules.CheckCharacter("Pat", true, 0, 4)?.Contains("is a player") == true, "a player character ran commands");
    Require(AdminCommandRules.CheckCharacter("Own", true, 6, 4)?.Contains("is Owner; this tab holds Administrator") == true, "a character above the tab's level ran");
    Require(AdminCommandRules.CheckCharacter("Gm", true, 2, 4) == null && AdminCommandRules.CheckCharacter("Ad", true, 4, 4) == null, "a staff character at or below the level was refused");
    Require(AdminCommandRules.ForLog("password Hunter2 Hunter2") == "[password ***" && AdminCommandRules.ForLog("[SetPassword x y") == "[SetPassword ***",
        "a password command kept its arguments");
    Require(AdminCommandRules.ForLog("go britain") == "[go britain", "ForLog changed a plain command");
    var cmdEntry = audit.Record("tester", "admin_command", AdminLevel.Counselor, true,
        JsonNode.Parse($"{{\"op\":\"admin_command\",\"text\":\"{AdminCommandRules.ForLog("[password Hunter2 Hunter2")}\"}}"));
    Require(!cmdEntry.ToJsonString().Contains("Hunter2") && !File.ReadAllText(auditPath).Contains("Hunter2"), "a command's password reached the audit");
    Console.WriteLine("PASS: the command ops need Counselor and the token; the dangerous list (shutdown, wipe, delete accounts, global decorate, mass moves) wants its typed word; run as only an online staff character at or below the level; no password in the audit");
    Console.WriteLine("ALL PASS");
}
finally
{
    try { Directory.Delete(home, true); } catch (IOException) { }
}
