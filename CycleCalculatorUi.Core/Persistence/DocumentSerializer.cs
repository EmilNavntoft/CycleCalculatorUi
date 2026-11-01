using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Persistence;

/// <summary>Reads and writes <see cref="SystemDocument"/>s as JSON (".cycle.json" files).</summary>
public static class DocumentSerializer
{
    public const string FileExtension = ".cycle.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    public static string Serialize(SystemDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Parses a document. Throws <see cref="InvalidDataException"/> for unreadable or newer files.</summary>
    public static SystemDocument Deserialize(string json)
    {
        SystemDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SystemDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid cycle document.", ex);
        }

        if (document is null)
            throw new InvalidDataException("The file is empty.");
        if (document.Version > SystemDocument.CurrentVersion)
            throw new InvalidDataException(
                $"The file was saved by a newer version of the editor (format {document.Version}).");

        document.Version = SystemDocument.CurrentVersion;
        return document;
    }

    public static SystemDocument Clone(SystemDocument document) =>
        JsonSerializer.Deserialize<SystemDocument>(JsonSerializer.Serialize(document, CompactOptions), CompactOptions)!;

    /// <summary>
    /// A hash of everything that affects the calculation. Layout (position, rotation, flip) is left out,
    /// so moving components does not make results stale.
    /// </summary>
    public static string Fingerprint(SystemDocument document)
    {
        SystemDocument copy = Clone(document);
        copy.Name = "";
        foreach (NodeModel node in copy.Nodes)
        {
            node.X = 0;
            node.Y = 0;
            node.Rotation = 0;
            node.FlipHorizontal = false;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, CompactOptions)));
        return Convert.ToHexString(hash, 0, 8);
    }
}
