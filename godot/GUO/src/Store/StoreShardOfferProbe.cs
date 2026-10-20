// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;

namespace GUO.Store;

/// <summary>The shard offer contract without a shard or a network (data_formats section 12, guo/shard-offer@1):
/// decode, refusals, feature blocks, the decision and the remembered answers. --shard-offer-probe.</summary>
public partial class StoreShardOfferProbe : Node
{
    public const string Flag = "--shard-offer-probe";

    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    private static string Descriptor(string hash) => JsonSerializer.Serialize(new
    {
        schema = "guo/shard-content@1",
        shard = new { name = "Probe Shard" },
        catalogues = new[] { new { url = "https://packs.example.com/", key = "ed25519:" + Convert.ToBase64String(new byte[32]) } },
        @lock = new { schema = "guo/content-lock@1", pack = "probe-pack", version = "1.0.0", identity_hash = hash, bindings = new { } },
    });

    private static byte[] Wire(string json) => new byte[] { StoreShardOffer.Format }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

    private static string Offer(string content = null, string extra = "") =>
        "{\"schema\":\"guo/shard-offer@1\",\"shard\":{\"name\":\"Probe Shard\"}"
        + (content != null ? ",\"content\":" + content : "")
        + ",\"packs\":[{\"id\":\"probe-pack\",\"version\":\"1.0.0\",\"title\":\"Probe pack\"}]"
        + ",\"features\":{\"water\":{\"allowed\":[\"classic\"],\"default\":\"classic\",\"lock\":false}}" + extra + "}";

    internal static bool Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-offer-probe-" + Guid.NewGuid().ToString("N"));
        string hash = new string('a', 64), other = new string('b', 64);
        string seen = null;
        void Feature(string name, string block) { if (name == "water") seen = block; }
        StoreShardOffer.FeatureOffered += Feature;
        try
        {
            var offer = StoreShardOffer.Decode(Wire(Offer(Descriptor(hash))));
            StorePack.Require(offer.Content.IdentityHash == hash && offer.Content.Url == "offer:" + hash, "the offered descriptor was not read");
            StorePack.Require(offer.Packs.Single().Title == "Probe pack", "the pack list was not read");
            offer.OfferFeatures(_ => { });
            StorePack.Require(seen != null && JsonDocument.Parse(seen).RootElement.GetProperty("default").GetString() == "classic", "the water block was not handed on");
            string question = offer.Question();
            StorePack.Require(question.Contains("Probe pack (probe-pack 1.0.0)") && question.Contains("packs.example.com (key ") && question.Contains("Nothing is downloaded"), "the question is missing what it asks for");

            var answers = new StoreShardOfferAnswers(temporary);
            string server = StoreShardOffer.ServerKey("Shard.Example.com ", 2593, "Probe Shard");
            StorePack.Require(server == "shard.example.com:2593|Probe Shard", "server key wrong: " + server);
            StorePack.Require(offer.Decide(server, null, answers) == StoreShardOffer.Decision.Ask, "a new offer must ask");
            StorePack.Require(offer.Decide(server, hash, answers) == StoreShardOffer.Decision.Mounted, "a mounted offer must not ask");
            answers.Decline(server, hash);
            StorePack.Require(new StoreShardOfferAnswers(temporary).Declined(server, hash), "a no was not remembered");
            StorePack.Require(offer.Decide(server, null, answers) == StoreShardOffer.Decision.Declined, "a declined offer asked again");
            StorePack.Require(StoreShardOffer.Decode(Wire(Offer(Descriptor(other)))).Decide(server, null, answers) == StoreShardOffer.Decision.Ask, "changed packs must ask again");
            StorePack.Require(offer.Decide("other:2593|Probe Shard", null, answers) == StoreShardOffer.Decision.Ask, "a no on one server applied to another");
            StorePack.Require(StoreShardOffer.Decode(Wire(Offer())).Decide(server, null, answers) == StoreShardOffer.Decision.NoPacks, "a features-only offer asked for packs");

            Refused(new byte[] { 2, (byte)'{', (byte)'}' }, "unknown offer format");
            Refused(Wire(Offer(Descriptor(hash), ",\"extra\":1")), "unknown offer field");
            Refused(Wire(Offer(Descriptor(hash)).Replace("\"water\":{", "\"water\":[{").Replace("false}}", "false}]}")), "feature block is a JSON object");
            Refused(Wire(Offer(Descriptor(hash)).Replace("\"probe-pack\",\"version\"", "\"Probe Pack\",\"version\"")), "Invalid pack id");
            Refused(Wire(Offer(Descriptor(hash)).Replace("\"lock\":false", "\"lock\":false,\"pad\":\"" + new string('x', StoreShardOffer.MaxFeature) + "\"")), "feature block too large");
            Refused(Wire(Offer(Descriptor("nothex"))), "");
            Refused(Wire("{\"schema\":\"guo/shard-offer@1\",\"schema\":\"x\"}"), "");
            Refused(Wire("not json"), "");
            Refused(new byte[StoreShardOffer.MaxJson + 2].Select((_, i) => i == 0 ? StoreShardOffer.Format : (byte)' ').ToArray(), "offer too large");
            StoreShardOfferPrompt.Receive(null, Wire("not json"));  // a bad offer is a log line, never a throw
            StoreShardOfferPrompt.Receive(null, Array.Empty<byte>());
            File.WriteAllText(Path.Combine(temporary, ".shard-offers.json"), "[null]");
            StorePack.Require(!answers.Declined(server, hash), "a null answer row was not ignored");
            File.WriteAllText(Path.Combine(temporary, ".shard-offers.json"), "not json");
            answers.Decline(server, hash);
            StorePack.Require(answers.Declined(server, hash), "an unreadable answers file could not be replaced");
            Refused(Wire(Offer(Descriptor(hash)).Replace("\"id\":\"probe-pack\"", "\"id\":\"other-pack\"")), "deployment root");

            GD.Print("[shard offer] PASS: decode, feature blocks, question, ask/mounted/declined/changed/features-only, remembered answers, nine refusals");
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[shard offer] FAIL: " + ex.Message); return false; }
        finally
        {
            StoreShardOffer.FeatureOffered -= Feature;
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static void Refused(byte[] wire, string why)
    {
        try { StoreShardOffer.Decode(wire); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException or ArgumentException
                                       or System.Collections.Generic.KeyNotFoundException)
        {
            StorePack.Require(why.Length == 0 || ex.Message.Contains(why, StringComparison.OrdinalIgnoreCase), $"refused for the wrong reason: {ex.Message} (wanted {why})");
            return;
        }
        throw new InvalidDataException("accepted an offer it should refuse: " + why);
    }
}
