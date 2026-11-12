using System.Text.Json;
using FluentAssertions;
using SharedKernel.StronglyTypedIds;
using Xunit;

namespace SharedKernel.Tests.StronglyTypedIds;

public class StronglyTypedUlidTests
{
    [Fact]
    public void NewId_WhenGenerating_ShouldHavePrefix()
    {
        // Arrange
        // Act
        var id = IdWithPrefix.NewId();

        // Assert
        id.Value.Should().StartWith("prefix_");
    }

    [Fact]
    public void TryParse_WhenValidId_ShouldSucceed()
    {
        // Arrange
        var id = IdWithPrefix.NewId();

        // Act
        var isParsedSuccessfully = IdWithPrefix.TryParse(id, out var result);

        // Assert
        isParsedSuccessfully.Should().BeTrue();
        result.Should().NotBeNull();
    }

    [Fact]
    public void TryParse_WhenParsedThroughIParsableAsFormBindingDoes_ShouldReturnTheSameId()
    {
        // Arrange
        var id = IdWithPrefix.NewId();

        // Act
        var isParsedSuccessfully = TryParseAsParsable<IdWithPrefix>(id.Value, out var result);

        // Assert
        isParsedSuccessfully.Should().BeTrue();
        result.Should().Be(id);
    }

    [Fact]
    public void TryParse_WhenParsedThroughIParsableWithInvalidValue_ShouldFail()
    {
        // Arrange
        // Act
        var isParsedSuccessfully = TryParseAsParsable<IdWithPrefix>("other_01ARZ3NDEKTSV4RRFFQ69G5FAV", out var result);

        // Assert
        isParsedSuccessfully.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void Parse_WhenParsedThroughIParsableWithInvalidValue_ShouldThrowFormatException()
    {
        // Arrange
        // Act
        var parse = () => ParseAsParsable<IdWithPrefix>("not-an-id");

        // Assert
        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void JsonConverter_WhenRoundTripping_ShouldReturnTheSameId()
    {
        // Arrange
        var options = new JsonSerializerOptions { Converters = { new StronglyTypedIdJsonConverter<string, IdWithPrefix>() } };
        var id = IdWithPrefix.NewId();

        // Act
        var json = JsonSerializer.Serialize(id, options);
        var result = JsonSerializer.Deserialize<IdWithPrefix>(json, options);

        // Assert
        json.Should().Be($"\"{id.Value}\"");
        result.Should().Be(id);
    }

    [Fact]
    public void JsonConverter_WhenTheValueIsNotAnIdOfTheType_ShouldThrowJsonException()
    {
        // Arrange
        var options = new JsonSerializerOptions { Converters = { new StronglyTypedIdJsonConverter<string, IdWithPrefix>() } };

        // Act
        var deserialize = () => JsonSerializer.Deserialize<IdWithPrefix>("\"other_01ARZ3NDEKTSV4RRFFQ69G5FAV\"", options);

        // Assert
        deserialize.Should().Throw<JsonException>();
    }

    private static bool TryParseAsParsable<T>(string value, out T? result) where T : IParsable<T>
    {
        return T.TryParse(value, null, out result);
    }

    private static T ParseAsParsable<T>(string value) where T : IParsable<T>
    {
        return T.Parse(value, null);
    }

    [IdPrefix("prefix")]
    public record IdWithPrefix(string Value) : StronglyTypedUlid<IdWithPrefix>(Value)
    {
        public override string ToString()
        {
            return Value;
        }
    }
}
