using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Transport;

namespace WordCraft.ObjectModel;

/// <summary>Main-story controls, with Word's one-based indexing and string IDs.</summary>
public sealed class ContentControls : IEnumerable
{
    readonly Document _doc;
    readonly string? _tag, _title;
    readonly ulong _document;
    internal ContentControls(Document doc, string? tag = null, string? title = null)
    {
        _doc = doc; _tag = tag; _title = title; _document = Read(doc).Document;
    }

    internal static (ulong Document, JsonObject[] Items) Read(Document doc)
    {
        var result = doc.Engine.Execute("document.contentControls") ?? throw new EngineException("document.contentControls", "no result");
        if (result["truncated"]?.GetValue<bool>() == true)
            throw new NotSupportedException("The document exceeds the 4096-control automation limit.");
        return (result["document"]?.GetValue<ulong>() ?? throw new NotSupportedException("The engine does not identify the document."),
            result["controls"]?.AsArray().OfType<JsonObject>().ToArray() ?? []);
    }
    JsonObject[] Items
    {
        get
        {
            var snapshot = Read(_doc);
            if (snapshot.Document != _document) throw new InvalidOperationException("The collection belongs to a different document.");
            return snapshot.Items.Where(c => (_tag is null || c["tag"]?.GetValue<string>() == _tag)
                && (_title is null || c["title"]?.GetValue<string>() == _title)).ToArray();
        }
    }
    public int Count => Items.Length;
    public ContentControl this[object index]
    {
        get
        {
            var items = Items;
            if (index is string id)
            {
                var matches = items.Where(c => c["id"]?.ToString() == id).ToArray();
                if (matches.Length != 1) throw new ArgumentException("Content control ID is missing or ambiguous.", nameof(index));
                return From(matches[0]);
            }
            int number = Convert.ToInt32(index, CultureInfo.InvariantCulture);
            if (number < 1 || number > items.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return From(items[number - 1]);
        }
    }
    ContentControl From(JsonObject item) => new(_doc, item["id"]?.GetValue<long>()
        ?? throw new NotSupportedException("This control has no stable ID."), _document);
    public IEnumerator GetEnumerator()
    {
        foreach (var item in Items) yield return From(item);
    }
}

/// <summary>A control is resolved by ID on every access, so edits cannot stale its positions.</summary>
public sealed class ContentControl
{
    readonly Document _doc;
    readonly long _id;
    readonly ulong _document;
    internal ContentControl(Document doc, long id, ulong document) { _doc = doc; _id = id; _document = document; }
    JsonObject Current
    {
        get
        {
            var snapshot = ContentControls.Read(_doc);
            var matches = snapshot.Items.Where(c => c["id"]?.GetValue<long>() == _id).ToArray();
            if (matches.Length != 1 || snapshot.Document != _document)
                throw new InvalidOperationException("The content control was removed, its ID is ambiguous, or its document changed.");
            return matches[0];
        }
    }
    public string ID => _id.ToString(CultureInfo.InvariantCulture);
    public string Title { get => Current["title"]?.GetValue<string>() ?? ""; set => Set("title", value); }
    public string Tag { get => Current["tag"]?.GetValue<string>() ?? ""; set => Set("tag", value); }
    public bool LockContents { get => Current["lockEdit"]?.GetValue<bool>() == true; set => Set("lockEdit", value); }
    public bool LockContentControl { get => Current["lockDelete"]?.GetValue<bool>() == true; set => Set("lockDelete", value); }
    void Set(string name, JsonNode? value)
    {
        _ = Current;
        _doc.Run("developer.properties", new JsonObject { ["id"] = _id, ["document"] = _document, [name] = value }, Effect.Props);
    }
    public bool Checked
    {
        get => Current["checked"]?.GetValue<bool>() ?? throw new InvalidOperationException("The content control is not a check box.");
        set
        {
            _ = Checked;
            _doc.Run("developer.toggleCheckBox", new JsonObject { ["id"] = _id, ["document"] = _document, ["value"] = value }, Effect.Text);
        }
    }
    public Range Range
    {
        get
        {
            var current = Current;
            var map = _doc.FreshMap;
            return new Range(_doc, map.FromPos(current["contentStart"]), map.FromPos(current["end"]));
        }
    }
}
