// Checks for the bridge's admin channel (ADR-0035): the token, the access
// levels, and that no password or token reaches the audit log.
using System.Text.Json.Nodes;
using GUO.EditorBridge;

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
    foreach (string mapOp in new[] { "hello", "block", "object", "multi", "mobiles", "command", "equip", null })
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
    Require(gm.OpsFor(AdminLevel.GameMaster).ToJsonString() == "[\"admin_whoami\",\"admin_status\",\"admin_godview\",\"admin_godview_find\"]", "OpsFor lists ops above the level");
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
    Console.WriteLine("ALL PASS");
}
finally
{
    try { Directory.Delete(home, true); } catch (IOException) { }
}
