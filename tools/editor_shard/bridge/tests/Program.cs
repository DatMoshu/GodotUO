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
    Require(gm.OpsFor(AdminLevel.GameMaster).ToJsonString() == "[\"admin_whoami\",\"admin_status\"]", "OpsFor lists ops above the level");
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
    Console.WriteLine("ALL PASS");
}
finally
{
    try { Directory.Delete(home, true); } catch (IOException) { }
}
