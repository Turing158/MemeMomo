using System.Text.Json.Serialization;

namespace MemeMomo.Models;

public sealed record TaskStatusDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TaskStatusCategory Category { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ColorKey { get; init; } = "Blue";
    public string IconKey { get; init; } = "Circle";
    public int Order { get; init; }
    public bool IsHidden { get; init; }
    [JsonIgnore]
    public bool IsBuiltIn => Enum.GetValues<TaskStatusCategory>().Any(category => Id == BuiltInId(category));
    [JsonIgnore]
    public bool IsClosed => Category is TaskStatusCategory.Done or TaskStatusCategory.Canceled;
    public static string BuiltInId(TaskStatusCategory category) => $"builtin-{category.ToString().ToLowerInvariant()}";
    public static string DefaultId => BuiltInId(TaskStatusCategory.Todo);
}
