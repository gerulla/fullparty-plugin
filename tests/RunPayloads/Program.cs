using System.Text.Json.Nodes;
using FullParty.Api;
using FullParty.Auth;
using FullParty.Models;

var assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition)
        throw new InvalidOperationException(message);
}

JsonNode Fixture(string name) => JsonNode.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json")))!;

JsonNode Slot(JsonNode json) => json["data"]!["roster"]!["slots"]![0]!;
JsonArray Fields(JsonNode json) => Slot(json)["field_values"]!.AsArray();

async Task<FullPartyRunDetail> Load(JsonNode json)
{
    var id = json["data"]!["id"]!.GetValue<int>();
    var auth = new AuthService(json.ToJsonString());
    var run = await new FullPartyApiClient(auth).GetRunDetailAsync(id, CancellationToken.None);
    Check(auth.LastPath == $"/api/xivplugin/runs/{id}", "Run endpoint must be unchanged.");
    return run ?? throw new InvalidOperationException("Run did not load.");
}

var ba = await Load(Fixture("ba"));
var baSlot = ba.Slots.Single();
Check(ba.Id == 7001 && ba.GroupId == 42 && ba.CanModerate, "BA run identity/permissions.");
Check(baSlot.CharacterClass == "WAR" && baSlot.CharacterClassRole == "tank", "BA class mapping.");
Check(baSlot.AssignedCharacter?.Name == "Aster Vale" && baSlot.AssignedCharacter.World == "Lich", "BA character identity.");
Check(baSlot.IsHost && baSlot.IsRaidLeader && baSlot.IsDarter && !baSlot.IsTrapper, "BA leadership/role flags.");
Check(baSlot.RaidPositionKey == "mt" && baSlot.RaidPosition == "Main Tank", "BA raid position must prefer localized label.");
Check(baSlot.FilledGroupLabel == null && baSlot.FilledGroupKey == null, "Empty localized arrays must stay empty.");
Check(baSlot.PhantomJob == null && baSlot.PhantomJobIconUrls.Count == 0, "BA must not invent a phantom job.");
Check(baSlot.ApplicationId == 61001 && baSlot.AttendanceStatus == "checked_in", "BA assignment state.");

var drs = await Load(Fixture("drs"));
var drsSlot = drs.Slots.Single();
Check(drsSlot.CharacterClass == "WHM" && drsSlot.CharacterClassRole == "healer", "DRS class mapping.");
Check(drsSlot.IsRaidLeader && drsSlot.IsTrapper && !drsSlot.IsHost && !drsSlot.IsDarter, "DRS role flags.");
Check(drsSlot.HolsterLoadout == new FullPartyRosterHolsterLoadout(31, 32, "Healer Pre-pop", "Healer Refill"), "DRS holster IDs and labels.");
Check(drsSlot.PhantomJob == null && drsSlot.RaidPosition == null, "Unconfigured fields must remain absent.");

foreach (var name in new[] { "ba", "drs" })
{
    var renamed = Fixture(name);
    foreach (var field in Fields(renamed))
        field!["field_key"] = $"configured_{field["field_key"]}";
    var mapped = (await Load(renamed)).Slots.Single();
    Check(mapped.CharacterClass == (name == "ba" ? "WAR" : "WHM"), "Classes resolve by source, not configured keys.");
    Check(name == "ba" ? mapped.RaidPosition == "Main Tank" : mapped.HolsterLoadout?.RefillId == 32, "Activity fields resolve by source.");

    var guest = Fixture(name);
    guest["data"]!["can_moderate"] = false;
    guest["data"]!["application_count"] = null;
    Slot(guest)["assignment"]!["application_id"] = null;
    var guestRun = await Load(guest);
    Check(!guestRun.CanModerate && guestRun.ApplicationCount == null && guestRun.Slots[0].ApplicationId == null, "Non-moderator nullable fields.");

    var empty = Fixture(name);
    Slot(empty)["assigned_character"] = null;
    Slot(empty)["assignment"] = null;
    Fields(empty)[0]!["value"] = null;
    var emptySlot = (await Load(empty)).Slots.Single();
    Check(emptySlot.AssignedCharacter == null && emptySlot.ApplicationId == null && emptySlot.CharacterClass == null, "Unoccupied slots.");
}

var standalone = Fixture("drs");
Fields(standalone)[1]!["value"]!["refill_id"] = null;
Fields(standalone)[1]!["value"]!["refill_label"] = null;
Check((await Load(standalone)).Slots[0].HolsterLoadout == new FullPartyRosterHolsterLoadout(31, null, "Healer Pre-pop", null), "Standalone pre-pop must not invent a refill.");

var legacy = Fixture("ba");
foreach (var field in Fields(legacy))
    field!.AsObject().Remove("source");
Check((await Load(legacy)).Slots[0].CharacterClass == "WAR", "Legacy responses without sources remain supported.");

var custom = Fixture("ba");
Fields(custom).Add(JsonNode.Parse("""{"field_key":"notes","source":"user","value":"Bring supplies"}"""));
Fields(custom).Add(JsonNode.Parse("""{"field_key":"count","value":2}"""));
Fields(custom).Add(JsonNode.Parse("""{"field_key":"choices","value":[1,2]}"""));
Fields(custom).Add(JsonNode.Parse("""{"field_key":"enabled","value":true}"""));
Check((await Load(custom)).Slots[0].CharacterClass == "WAR", "Unrelated scalar/array fields must not break deserialization.");

var noClass = Fixture("ba");
Fields(noClass)[0]!["source"] = "unrelated";
Check((await Load(noClass)).Slots[0].CharacterClass == null, "A conflicting source must not be mistaken for a character class.");

var occult = Fixture("ba");
Fields(occult).Add(JsonNode.Parse("""
    {"field_key":"custom_phantom","source":"phantom_jobs","value":{
      "id":12,"name":"Geomancer","max_level":20,"icon_id":123,
      "icon_url":"https://example.invalid/geomancer.png"}}
    """));
var occultSlot = (await Load(occult)).Slots.Single();
Check(occultSlot.PhantomJob == "Geomancer" && occultSlot.PhantomJobMaxLevel == 20 && occultSlot.PhantomJobIconId == 123, "Existing phantom data still maps.");

var filled = Fixture("ba");
Slot(filled)["is_duelist"] = true;
Slot(filled)["slot_kind"] = "fill_in";
Slot(filled)["is_fill_in"] = true;
Slot(filled)["filled_group_key"] = "party-b";
Slot(filled)["filled_group_label"] = JsonNode.Parse("""{"en":"Party B"}""");
var fillin = (await Load(filled)).Slots.Single();
Check(fillin.IsDuelist && fillin.IsFillIn && fillin.FilledGroupLabel == "Party B", "Additional flags and existing fill-ins survive mapping.");

foreach (var (name, partyCount) in new[] { ("ba", 7), ("drs", 6) })
{
    var full = Fixture(name);
    var prototype = Slot(full).DeepClone();
    var slots = full["data"]!["roster"]!["slots"]!.AsArray();
    slots.Clear();
    for (var party = 0; party < partyCount; party++)
    {
        for (var position = 1; position <= 8; position++)
        {
            var slot = prototype.DeepClone();
            slot["id"] = 10000 + party * 8 + position;
            slot["group_key"] = $"party-{(char)('a' + party)}";
            slot["group_label"] = new JsonObject { ["en"] = $"Party {(char)('A' + party)}" };
            slot["position_in_group"] = position;
            if (position > 1)
            {
                slot["assigned_character"] = null;
                slot["assignment"] = null;
            }
            slots.Add(slot);
        }
    }
    var mapped = await Load(full);
    Check(mapped.Slots.Count == partyCount * 8, $"{name} full-size roster must not be truncated.");
    Check(mapped.Slots.Select(slot => slot.GroupKey).Distinct().Count() == partyCount, $"{name} must retain every party.");
}

Console.WriteLine($"Run payload regression checks passed ({assertions} assertions).");
