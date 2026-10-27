using System.Text.Json.Nodes;

namespace InvoiceProcessing.Tools;

/// <summary>
/// Deterministic clean-up of the raw Document Intelligence JSON. Drops everything the extraction agent
/// does not need (page geometry, polygons, spans, words, per-field bounding regions) and flattens each
/// field to <c>{ value, confidence }</c> so the prompt stays small and the model is not distracted.
/// </summary>
public static class InvoiceJsonReducer
{
    /// <summary>Confidence is kept only when it is low enough for the agent to care about.</summary>
    private const double ConfidenceReportThreshold = 0.9;

    public static JsonObject Reduce(JsonNode analyzeResult)
    {
        var reduced = new JsonObject();
        var document = analyzeResult["documents"]?.AsArray().FirstOrDefault();
        if (document is null)
        {
            reduced["fields"] = new JsonObject();
            return reduced;
        }

        reduced["docType"] = document["docType"]?.DeepClone();
        reduced["documentConfidence"] = document["confidence"]?.DeepClone();

        var fields = new JsonObject();
        if (document["fields"] is JsonObject src)
        {
            foreach (var (name, field) in src)
            {
                var value = ReduceField(field);
                if (value is not null) fields[name] = value;
            }
        }
        reduced["fields"] = fields;
        return reduced;
    }

    private static JsonNode? ReduceField(JsonNode? field)
    {
        if (field is not JsonObject obj) return null;

        var value = ReduceValue(obj);
        if (value is null) return null;

        // Only surface confidence when it is questionable; high-confidence noise is dropped.
        if (obj["confidence"]?.GetValue<double>() is double c && c < ConfidenceReportThreshold)
        {
            return new JsonObject { ["value"] = value, ["confidence"] = Math.Round(c, 2) };
        }
        return value;
    }

    private static JsonNode? ReduceValue(JsonObject field)
    {
        switch (field["type"]?.GetValue<string>())
        {
            case "array":
                var items = new JsonArray();
                foreach (var item in field["valueArray"]?.AsArray() ?? [])
                {
                    var reducedItem = ReduceField(item);
                    if (reducedItem is not null) items.Add(reducedItem);
                }
                return items.Count == 0 ? null : items;

            case "object":
                var result = new JsonObject();
                foreach (var (name, child) in field["valueObject"]?.AsObject() ?? [])
                {
                    var reducedChild = ReduceField(child);
                    if (reducedChild is not null) result[name] = reducedChild;
                }
                return result.Count == 0 ? null : result;

            case "currency":
                var currency = field["valueCurrency"];
                if (currency?["amount"] is null) return null;
                return new JsonObject
                {
                    ["amount"] = currency["amount"]!.DeepClone(),
                    ["currencyCode"] = currency["currencyCode"]?.DeepClone(),
                };

            case "address":
                // The formatted content is what a human would read; the structured parts add little.
                return field["content"]?.DeepClone();

            case "string": return field["valueString"]?.DeepClone() ?? field["content"]?.DeepClone();
            case "date": return field["valueDate"]?.DeepClone() ?? field["content"]?.DeepClone();
            case "number": return field["valueNumber"]?.DeepClone() ?? field["content"]?.DeepClone();
            case "integer": return field["valueInteger"]?.DeepClone() ?? field["content"]?.DeepClone();
            case "phoneNumber": return field["valuePhoneNumber"]?.DeepClone() ?? field["content"]?.DeepClone();
            case "countryRegion": return field["valueCountryRegion"]?.DeepClone() ?? field["content"]?.DeepClone();
            default: return field["content"]?.DeepClone();
        }
    }
}
