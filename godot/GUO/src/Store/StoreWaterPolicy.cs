// SPDX-License-Identifier: BSD-2-Clause
// ADR-0037 decision 8: a shard offers water settings; only the player consents.
using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.Store;

internal sealed class StoreWaterPolicy
{
    [JsonPropertyName("default")] public string Default { get; init; } = "classic";
    [JsonPropertyName("allowed")] public string[] Allowed { get; init; } = Array.Empty<string>();
    [JsonPropertyName("lock")] public bool Lock { get; init; }

    public static StoreWaterPolicy Parse(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object, "water must be an object");
        var policy = JsonSerializer.Deserialize<StoreWaterPolicy>(value.GetRawText());
        policy.Validate();
        return policy;
    }

    public void Validate()
    {
        Require(ValidStyle(Default), "Invalid default water style");
        Require(Allowed != null && Allowed.Length <= 64 && Allowed.All(ValidStyle)
            && Allowed.Distinct(StringComparer.OrdinalIgnoreCase).Count() == Allowed.Length,
            "Invalid or duplicate allowed water styles (maximum 64)");
        Require(Allowed.Length == 0 || Allowed.Contains(Default, StringComparer.OrdinalIgnoreCase), "The default water style must be allowed");
    }

    private static void Require(bool valid, string reason) { if (!valid) throw new InvalidDataException(reason); }

    private static bool ValidStyle(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return false;
        if (id.Equals("classic", StringComparison.OrdinalIgnoreCase) || id.Equals("uo", StringComparison.OrdinalIgnoreCase)) return true;
        string[] parts = id.Split(':');
        return parts.Length == 2 && parts.All(part => part.Length > 0
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    // An empty allowlist with lock means only the shard's default. Without
    // lock, allowed is advisory: the player may keep any installed style.
    public bool Allows(string id)
    {
        if (!Lock) return true;
        if (Allowed.Length == 0) return string.Equals(id, Default, StringComparison.OrdinalIgnoreCase);
        foreach (string allowed in Allowed)
            if (string.Equals(id, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public string Select(string choice) => string.IsNullOrEmpty(choice) || !Allows(choice) ? Default : choice;

    /// <summary>Consent binds to this descriptor and policy, independently of a pack deployment's identity.</summary>
    public string ConsentKey(string source)
    {
        string canonical = source + "\n" + Default.ToLowerInvariant() + "\n" + Lock + "\n"
            + string.Join('\n', Allowed.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
