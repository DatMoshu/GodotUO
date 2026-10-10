using System.Text.Json.Nodes;
using GUO.Editor;
using GUO.Workspace;

/// <summary>
/// The Admin tab's Settings form without the engine (AD4): the ModernUO schema, a server's configuration files
/// loaded, checked, compared and written, the previous files kept with secrets masked, secrets kept in the
/// secrets file only.
/// </summary>
internal static class SettingsTests
{
    private static void Require(bool ok, string what) { if (!ok) throw new Exception("settings: " + what); }

    public static void Run(string home)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "godot", "GUO", "project.godot"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("repository not found");
        var schema = SettingsSchema.Parse(File.ReadAllText(Path.Combine(repo, "godot", "GUO", "addons", "guo_editor", "Admin", "Schemas", "modernuo.settings.json")));
        Require(schema.Backend == "modernuo" && schema.Groups.Count >= 8, "schema groups");

        // A server folder from GUO's own templates, plus the files ModernUO makes on first boot.
        string server = Path.Combine(home, "settings server"), config = Path.Combine(server, "Configuration");
        Directory.CreateDirectory(config); Directory.CreateDirectory(Path.Combine(server, "Data"));
        string templates = Path.Combine(repo, "tools", "modernuo", "config");
        File.WriteAllText(Path.Combine(config, "modernuo.json"), File.ReadAllText(Path.Combine(templates, "modernuo.template.json"))
            .Replace("@UO_CLIENT_DATA@", home.Replace("\\", "\\\\")).Replace("@UO_SHARD_NAME@", "Test Shard").Replace("@UO_SHARD_LISTENER@", "127.0.0.1:2593"));
        File.Copy(Path.Combine(templates, "expansion.json"), Path.Combine(config, "expansion.json"));
        const string oldMail = "Old Mail Secret 1", newMail = "New Mail Secret 2", hook = "https://example.invalid/hook/Hook3Secret";
        File.WriteAllText(Path.Combine(config, "email-settings.json"), "{\n  \"enabled\": false,\n  \"fromAddress\": \"a@example.com\",\n  \"crashAddress\": \"c@example.com\",\n  \"speechLogPageAddress\": \"p@example.com\",\n  \"emailServer\": \"smtp.example.com\",\n  \"emailPort\": 465,\n  \"emailUsername\": \"a@example.com\",\n  \"emailPassword\": \"" + oldMail + "\"\n}\n");
        File.WriteAllText(Path.Combine(config, "server-access.json"), "{\n  \"protectedAccounts\": [\n    \"owner\"\n  ]\n}\n");
        var table = new JsonArray();
        string[] names = { "None", "The Second Age", "Renaissance", "Third Dawn", "Blackthorn's Revenge", "Age of Shadows", "Samurai Empire", "Mondain's Legacy", "Stygian Abyss", "High Seas", "Time of Legends", "Endless Journey" };
        for (int i = 0; i < names.Length; i++)
            table.Add(new JsonObject { ["Id"] = i, ["Name"] = names[i], ["SupportedFeatures"] = new JsonObject { ["T2A"] = i >= 1 }, ["MapSelectionFlags"] = new JsonObject { ["Felucca"] = true, ["Trammel"] = i >= 2 }, ["MobileStatusVersion"] = i });
        File.WriteAllText(Path.Combine(server, "Data", "expansions.json"), table.ToJsonString());
        string secretsFile = Path.Combine(home, "settings workspace", "shard", "secrets.bat");
        ShardSecrets.Write("UO_SHARD_OWNER_PASSWORD", "Owner1", secretsFile);

        Require(SettingsSchema.BackendFor("custom", server) == "modernuo" && SettingsSchema.BackendFor("custom", home) == null, "backend from the folder");
        var s = ServerSettings.Load(schema, server, key => ShardSecrets.ReadFile(key, secretsFile));
        Require(s.MissingFiles.SequenceEqual(new[] { "Configuration/crowdsec.json" }), "missing files: " + string.Join(",", s.MissingFiles));
        SettingsField F(string id) => s.Field(id) ?? throw new Exception("settings: no field " + id);
        SettingsField perIp = F("modernuo:settings/accountHandler.maxAccountsPerIP"), delay = F("modernuo:settings/autosave.saveDelay"),
            name = F("modernuo:settings/serverListing.serverName"), listen = F("modernuo:listeners"), mail = F("email:emailPassword"),
            webhook = F("modernuo:settings/pages.discordWebhookUrl"), expansion = F("expansion:Id"), fel = F("expansion:MapSelectionFlags/Felucca"),
            port = F("email:emailPort"), prot = F("access:protectedAccounts"), test = F("modernuo:settings/testCenter.enable");
        Require(s.Get(perIp) == "16" && s.Get(delay) == "00:05:00" && s.Get(name) == "Test Shard" && s.Get(listen) == "127.0.0.1:2593"
            && s.Get(expansion) == "EJ" && s.Get(fel) == "True" && s.Get(port) == "465" && s.Get(prot) == "owner", "loaded values");
        // The form covers the whole of modernuo.json: lines with no label are in the other group, by their own name.
        var other = s.Groups.Single(g => g.Name == schema.OtherGroup).Fields;
        Require(other.Any(f => f.Label == "network.maxBufferSlabs" && f.Type == "int") && other.Any(f => f.Label == "autosave.warningDelay") == false
            && other.Any(f => f.Label == "movementThrottle.maxChainGap") && other.All(f => f.IsOther), "the other group");
        int lines = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "modernuo.json")))["settings"].AsObject().Count;
        Require(s.Fields.Count(f => f.File == "modernuo" && f.Path.StartsWith("settings/")) == lines, "every settings line on the form");
        // A secret is never handed to the form: only whether it is set.
        Require(s.Get(mail) == null && s.SecretSet(mail) && !s.SecretSet(webhook) && !s.SecretKept(mail) && s.Diff().Count == 0, "secrets hidden, no diff at load");
        Require(ServerSettings.LooksSecret("pages.discordWebhookUrl") && ServerSettings.LooksSecret("emailPassword") && !ServerSettings.LooksSecret("emailPort"), "secret names");

        // Validation, in plain words, on what changed.
        s.Set(perIp, "-1"); s.Set(delay, "soon"); s.Set(listen, "localhost"); s.Set(name, "");
        var errors = s.Validate();
        Require(errors[perIp.Id] == "must be at least 1" && errors[delay.Id].StartsWith("must be a time") && errors[listen.Id].Contains("not an address")
            && errors[name.Id] == "may not be empty" && errors.Count == 4, "validation: " + string.Join("; ", errors.Select(e => e.Key + "=" + e.Value)));
        bool refused = false; try { s.Write((k, v) => ShardSecrets.Write(k, v, secretsFile), DateTime.UtcNow); } catch (InvalidDataException) { refused = true; }
        Require(refused && File.ReadAllText(Path.Combine(config, "modernuo.json")).Contains("\"16\""), "an invalid form was written");
        s.Set(delay, "00:00:30"); Require(s.Validate()[delay.Id] == "must be at least 00:01:00", "timespan minimum");
        s.SetSecret(mail, "bad\"quote"); Require(s.Validate()[mail.Id].Contains("may not hold"), "a secret with a quote");

        // A good form: the diff, then the write.
        s.Set(perIp, "15"); s.Set(delay, "00:07:00"); s.Set(name, "Settings Test"); s.Set(listen, "127.0.0.1:2593\r\n0.0.0.0:2600\n");
        s.Set(expansion, "SE"); s.Set(fel, "False"); s.Set(port, "587"); s.Set(prot, "owner\nsecond"); s.Set(test, "True");
        s.SetSecret(mail, newMail);
        Require(s.Validate().Count == 0, "valid form refused: " + string.Join("; ", s.Validate().Values));
        Require(s.Warnings().Single().Contains("0.0.0.0:2600"), "an open listener warns");
        var diff = s.Diff();
        string described = string.Join("\n", diff.Select(c => c.Describe()));
        Require(diff.Count == 10 && described.Contains("Accounts: Accounts per address: 16 -> 15") && described.Contains("Expansion and maps: Expansion: Endless Journey -> Samurai Empire")
            && described.Contains("Felucca: on -> off") && described.Contains("Email: Mail password: changed (kept in your secrets file)")
            && !described.Contains(newMail) && !described.Contains(oldMail), "diff:\n" + described);
        string audit = s.AuditChanges().ToJsonString();
        Require(!audit.Contains(newMail) && !audit.Contains(oldMail) && audit.Contains("\"secret\":true") && audit.Contains("\"to\":\"15\""), "audit changes");

        var result = s.Write((k, v) => ShardSecrets.Write(k, v, secretsFile), new DateTime(2026, 10, 10, 3, 4, 5, DateTimeKind.Utc));
        Require(result.Files.Count == 4 && result.SecretsWritten.SequenceEqual(new[] { "UO_SHARD_EMAIL_PASSWORD" }), "written files: " + string.Join(",", result.Files));
        JsonNode main = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "modernuo.json")));
        Require((string)main["settings"]["accountHandler.maxAccountsPerIP"] == "15" && (string)main["settings"]["autosave.saveDelay"] == "00:07:00"
            && (string)main["settings"]["testCenter.enable"] == "True" && main["listeners"].AsArray().Count == 2
            && string.Join(",", main.AsObject().Select(kv => kv.Key)) == "assemblyDirectories,dataDirectories,listeners,settings"
            && main["settings"]["pages.discordWebhookUrl"] == null && main["settings"].AsObject().ContainsKey("pages.discordWebhookUrl"), "modernuo.json written");
        Require(!File.ReadAllText(Path.Combine(config, "modernuo.json")).Contains("\r"), "LF line ends");
        JsonNode exp = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "expansion.json")));
        Require((int)exp["Id"] == 6 && (string)exp["Name"] == "Samurai Empire" && (bool)exp["MapSelectionFlags"]["Felucca"] == false
            && (bool)exp["MapSelectionFlags"]["Tokuno"] == true && (int)exp["MobileStatusVersion"] == 6, "expansion.json from the server's table, facets kept");
        JsonNode email = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "email-settings.json")));
        Require((string)email["emailPassword"] == newMail && email["emailPort"].GetValueKind() == System.Text.Json.JsonValueKind.Number && (int)email["emailPort"] == 587, "email-settings.json written, kinds kept");
        Require(JsonNode.Parse(File.ReadAllText(Path.Combine(config, "server-access.json")))["protectedAccounts"].AsArray().Count == 2, "list written");
        // The previous files are kept, with every secret masked.
        Require(result.PreviousFolder.EndsWith(Path.Combine("GUO-previous", "20261010-030405Z")) && Directory.GetFiles(result.PreviousFolder).Length == 4, "previous files kept");
        string prevMail = File.ReadAllText(Path.Combine(result.PreviousFolder, "email-settings.json"));
        Require(prevMail.Contains("\"***\"") && !prevMail.Contains(oldMail) && File.ReadAllText(Path.Combine(result.PreviousFolder, "modernuo.json")).Contains("\"16\""), "previous copies");
        // The secret is in the secrets file, which kept its other lines.
        Require(ShardSecrets.ReadFile("UO_SHARD_EMAIL_PASSWORD", secretsFile) == newMail && ShardSecrets.ReadFile("UO_SHARD_OWNER_PASSWORD", secretsFile) == "Owner1", "secrets file");
        string[] everywhere = Directory.GetFiles(server, "*", SearchOption.AllDirectories);
        Require(everywhere.Count(f => File.ReadAllText(f).Contains(newMail)) == 1, "the secret is in the server's own file once, nowhere else in its folder");

        // Reloaded: nothing to save. The secrets file is where GUO keeps a secret: a value only there is a change to write.
        var again = ServerSettings.Load(schema, server, key => ShardSecrets.ReadFile(key, secretsFile));
        Require(again.Diff().Count == 0 && again.Get(again.Field(perIp.Id)) == "15", "reloaded");
        ShardSecrets.Write("UO_SHARD_DISCORD_WEBHOOK", hook, secretsFile);
        again = ServerSettings.Load(schema, server, key => ShardSecrets.ReadFile(key, secretsFile));
        var sync = again.Diff();
        Require(sync.Count == 1 && sync[0].Field.Path == "settings/pages.discordWebhookUrl" && !sync[0].Describe().Contains(hook), "secrets file to server sync");
        again.Write((k, v) => ShardSecrets.Write(k, v, secretsFile), new DateTime(2026, 10, 10, 3, 4, 6, DateTimeKind.Utc));
        Require((string)JsonNode.Parse(File.ReadAllText(Path.Combine(config, "modernuo.json")))["settings"]["pages.discordWebhookUrl"] == hook, "webhook written");
        // Clearing a secret clears both.
        again = ServerSettings.Load(schema, server, key => ShardSecrets.ReadFile(key, secretsFile));
        again.SetSecret(again.Field(mail.Id), "");
        Require(again.Diff().Single().Describe().EndsWith("cleared"), "clear described");
        again.Write((k, v) => ShardSecrets.Write(k, v, secretsFile), new DateTime(2026, 10, 10, 3, 4, 7, DateTimeKind.Utc));
        Require(ShardSecrets.ReadFile("UO_SHARD_EMAIL_PASSWORD", secretsFile) == null && JsonNode.Parse(File.ReadAllText(Path.Combine(config, "email-settings.json")))["emailPassword"] == null
            && ShardSecrets.ReadFile("UO_SHARD_DISCORD_WEBHOOK", secretsFile) == hook, "secret cleared");
        // Only the last ten sets of previous files are kept.
        for (int i = 0; i < 12; i++)
        {
            var more = ServerSettings.Load(schema, server, key => ShardSecrets.ReadFile(key, secretsFile));
            more.Set(more.Field(perIp.Id), (20 + i).ToString());
            more.Write(null, new DateTime(2026, 10, 10, 4, 0, i, DateTimeKind.Utc));
        }

        Require(Directory.GetDirectories(Path.Combine(config, "GUO-previous")).Length == ServerSettings.KeepPrevious, "previous sets pruned");
        Console.WriteLine("PASS: the Admin tab's Settings form: schema, every modernuo.json line, validation, diff, write, previous kept, secrets only in the secrets file (AD4)");
    }
}
