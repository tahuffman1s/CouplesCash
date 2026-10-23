using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouplesCash.Api.Contracts;

/// <summary>
/// A PATCH field that distinguishes "not sent" (<see cref="HasValue"/> = false) from "sent as null"
/// (<see cref="HasValue"/> = true, <see cref="Value"/> = null), so nullable settings can be cleared.
/// </summary>
public readonly record struct Optional<T>(bool HasValue, T? Value)
{
    public static Optional<T> Of(T? value) => new(true, value);

    public static readonly Optional<T> Missing = default;
}

/// <summary>Reads any JSON value (including null) into <c>Optional&lt;T&gt;</c> with <c>HasValue = true</c>; a missing property stays <c>default</c>.</summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(inner))!;
    }

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? new Optional<T>(true, default)
                : new Optional<T>(true, JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (!value.HasValue || value.Value is null)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
