using System.Text.Json.Nodes;

namespace KLink.Server;

/// <summary>卡组代码解析与对局发牌（DeckCodeManager）。</summary>
public sealed class DeckCodeManager
{
    private readonly AssetStore _assets;

    public DeckCodeManager(AssetStore assets) => _assets = assets;

    public JsonObject ParseDeckCode(string? deckCode)
    {
        var result = new JsonObject();
        try
        {
            if (deckCode is null || !deckCode.StartsWith("%%"))
                return FallbackDeck("Deck code must start with %%");
            string code = deckCode[2..];
            string[] parts = code.Split('|');
            if (parts.Length < 2)
                return FallbackDeck("Deck code missing card part");
            string country = parts[0];
            string cards = parts[1];
            string hq = parts.Length >= 3 ? parts[2] : "0N";
            if (country.Length < 2)
                return FallbackDeck("Bad country code");
            int tilde = cards.IndexOf('~');
            if (tilde >= 0)
                cards = cards[..tilde];
            string[] groups = cards.Split(';');
            if (groups.Length != 4)
                return FallbackDeck("Bad card groups");
            int[] multipliers = { 1, 2, 3, 4 };
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < groups.Length; i++)
            {
                string group = groups[i];
                for (int j = 0; j + 1 < group.Length; j += 2)
                {
                    string importId = group.Substring(j, 2);
                    counts.TryGetValue(importId, out int old);
                    counts[importId] = old + multipliers[i];
                }
            }
            var importIds = new JsonObject();
            int total = 0;
            foreach (var (key, count) in counts)
            {
                importIds[key] = count;
                total += count;
            }
            result["success"] = true;
            result["main_country"] = CountryName(country.Substring(0, 1));
            result["ally_country"] = CountryName(country.Substring(1, 1));
            result["import_ids"] = importIds;
            result["total_cards"] = total;
            result["unique_cards"] = counts.Count;
            result["deck_code"] = deckCode;
            result["hq_code"] = hq;
            return result;
        }
        catch (Exception e)
        {
            return FallbackDeck(e.ToString());
        }
    }

    public JsonArray CreateMatchCards(string side, JsonObject deckData)
    {
        var cards = new JsonArray();
        JsonNode deckCodeIds = _assets.DeckCodeIds();
        string hqCode = deckData["hq_code"]?.GetValue<string>() ?? "0N";
        if (hqCode.Length > 2)
            hqCode = hqCode[..2];
        string mainCountry = deckData["main_country"]?.GetValue<string>() ?? "Britain";
        bool left = side == "left";
        var hq = new JsonObject
        {
            ["card_id"] = left ? 1 : 41,
            ["faction"] = mainCountry,
            ["is_gold"] = true,
            ["location"] = left ? "board_hqleft" : "board_hqright",
            ["location_number"] = 0,
            ["name"] = CardName(deckCodeIds, hqCode, "card_location_london"),
        };
        cards.Add(hq);
        int cardId = left ? 2 : 42;
        int locationNumber = 0;
        string location = left ? "deck_left" : "deck_right";
        var importIds = deckData["import_ids"] as JsonObject;
        if (importIds is not null)
        {
            foreach (var (key, value) in importIds)
            {
                int count = value?.GetValue<int>() ?? 0;
                for (int i = 0; i < count; i++)
                {
                    cards.Add(new JsonObject
                    {
                        ["card_id"] = cardId++,
                        ["is_gold"] = true,
                        ["location"] = location,
                        ["location_number"] = locationNumber++,
                        ["name"] = CardName(deckCodeIds, key, "card_unknown"),
                    });
                }
            }
        }
        return cards;
    }

    private static JsonObject FallbackDeck(string error) => new()
    {
        ["success"] = false,
        ["error"] = error,
        ["main_country"] = "Britain",
        ["ally_country"] = "USA",
        ["import_ids"] = new JsonObject(),
        ["total_cards"] = 0,
        ["unique_cards"] = 0,
        ["deck_code"] = "",
        ["hq_code"] = "0N",
    };

    private static string CountryName(string code) => code switch
    {
        "1" => "Germany",
        "2" => "Britain",
        "3" => "Japan",
        "4" => "Soviet",
        "5" => "USA",
        "6" => "France",
        "7" => "Italy",
        "8" => "Poland",
        "9" => "Finland",
        _ => "Unknown",
    };

    private static string CardName(JsonNode table, string key, string fallback)
    {
        var item = table[key] as JsonObject;
        return item?["card"]?.GetValue<string>() ?? fallback;
    }
}
