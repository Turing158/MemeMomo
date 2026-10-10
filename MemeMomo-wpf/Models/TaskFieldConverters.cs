using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemeMomo.Models;

// Compatibility is deliberately limited to the newly introduced fields.
public sealed class MemoKindConverter : JsonConverter<MemoKind>
{
    public override MemoKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return TaskFieldParsing.ReadEnum<MemoKind>(value.RootElement) ?? MemoKind.Note;
    }
    public override void Write(Utf8JsonWriter writer, MemoKind value, JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
}

public sealed class TaskCategorySnapshotConverter : JsonConverter<TaskStatusCategory?>
{
    public override TaskStatusCategory? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return TaskFieldParsing.ReadEnum<TaskStatusCategory>(value.RootElement);
    }
    public override void Write(Utf8JsonWriter writer, TaskStatusCategory? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue((int)value.Value);
        else writer.WriteNullValue();
    }
}

public sealed class TaskReferenceConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString() : null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed class TaskClosedAtConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.ValueKind == JsonValueKind.String && value.RootElement.TryGetDateTime(out var date) ? date : null;
    }
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(value.Value);
        else writer.WriteNullValue();
    }
}

internal static class TaskFieldParsing
{
    internal static T? ReadEnum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
            return (T)Enum.ToObject(typeof(T), number);
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<T>(value.GetString(), true, out var result) && Enum.IsDefined(result))
            return result;
        return null;
    }
}
