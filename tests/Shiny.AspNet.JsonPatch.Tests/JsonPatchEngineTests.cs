using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Shiny.AspNet.JsonPatch.Tests;

// ---------------------------------------------------------------------------
// RFC 6902 JSON Patch over RFC 6901 JSON Pointer. The engine is checked against
// the RFC's own worked examples and the cases from the community json-patch
// test suite (github.com/json-patch/json-patch-tests) that pin down the edges:
// leading-zero indexes, "-", escaping order, number equality, atomicity. Then
// the same patch goes over the wire — a RequestDelegate handler and a
// minimal-API parameter — because a correct engine behind the wrong status
// code is still the wrong answer to a client.
// ---------------------------------------------------------------------------

public record PatchWidget(int Id, string Name, List<string> Tags, decimal Price);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PatchWidget))]
public partial class PatchJson : JsonSerializerContext;

public class JsonPointerTests
{
    // RFC 6901 §5, every example, against the RFC's own document.
    const string Rfc6901Document = """
        {
          "foo": ["bar", "baz"],
          "": 0,
          "a/b": 1,
          "c%d": 2,
          "e^f": 3,
          "g|h": 4,
          "i\\j": 5,
          "k\"l": 6,
          " ": 7,
          "m~n": 8
        }
        """;

    [Theory]
    [InlineData("/foo", """["bar","baz"]""")]
    [InlineData("/foo/0", "\"bar\"")]
    [InlineData("/", "0")]
    [InlineData("/a~1b", "1")]
    [InlineData("/c%d", "2")]
    [InlineData("/e^f", "3")]
    [InlineData("/g|h", "4")]
    [InlineData("/i\\j", "5")]
    [InlineData("/k\"l", "6")]
    [InlineData("/ ", "7")]
    [InlineData("/m~0n", "8")]
    public void Resolves_the_rfc_6901_examples(string pointer, string expected)
    {
        var document = JsonNode.Parse(Rfc6901Document);

        Assert.True(JsonPointer.Parse(pointer).TryEvaluate(document, out var value));
        Assert.Equal(expected, value!.ToJsonString());
    }

    [Fact]
    public void The_empty_pointer_is_the_whole_document_and_slash_is_the_empty_member()
    {
        var document = JsonNode.Parse(Rfc6901Document);

        Assert.True(JsonPointer.Root.TryEvaluate(document, out var whole));
        Assert.Same(document, whole);

        Assert.Equal([""], JsonPointer.Parse("/").Segments);
        Assert.Empty(JsonPointer.Parse("").Segments);
    }

    [Fact]
    public void Decodes_tilde_one_before_tilde_zero()
    {
        // "~01" is "~1", not "/": decoding ~0 first would produce "~1" and then "/".
        Assert.Equal(["~1"], JsonPointer.Parse("/~01").Segments);
        Assert.Equal(["a/b", "m~n"], JsonPointer.Parse("/a~1b/m~0n").Segments);
    }

    [Fact]
    public void Round_trips_segments_through_escaping()
    {
        var pointer = JsonPointer.Create("a/b", "~", "0");

        Assert.Equal("/a~1b/~0/0", pointer.ToString());
        Assert.Equal(pointer, JsonPointer.Parse(pointer.ToString()));
        Assert.Equal("/a~1b/~0/0/-", pointer.Append("-").ToString());
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/a~")]
    public void Refuses_invalid_pointers(string pointer)
    {
        Assert.False(JsonPointer.TryParse(pointer, out _));
        Assert.Throws<FormatException>(() => JsonPointer.Parse(pointer));
    }

    [Theory]
    [InlineData("/foo/01")]
    [InlineData("/foo/-")]
    [InlineData("/foo/2")]
    [InlineData("/foo/-1")]
    [InlineData("/foo/0/x")]
    [InlineData("/missing")]
    public void Reports_a_missing_or_invalid_target_as_absent(string pointer)
    {
        Assert.False(JsonPointer.Parse(pointer).TryEvaluate(JsonNode.Parse(Rfc6901Document), out _));
    }

    [Fact]
    public void Tells_a_present_null_from_an_absent_member()
    {
        var document = JsonNode.Parse("""{"a":null}""");

        Assert.True(JsonPointer.Parse("/a").TryEvaluate(document, out var value));
        Assert.Null(value);
        Assert.False(JsonPointer.Parse("/b").TryEvaluate(document, out _));
    }
}

public class JsonPatchEngineTests
{
    static void AssertJsonEqual(string expected, JsonNode? actual)
        => Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"Expected {expected} but got {actual?.ToJsonString() ?? "null"}"
        );

    static JsonNode? Apply(string document, string patch)
        => JsonPatchDocument.Parse(patch).ApplyTo(JsonNode.Parse(document));

    /// <summary>RFC 6902 Appendix A — every example that is expected to succeed.</summary>
    public static TheoryData<string, string, string, string> AppendixSuccesses => new()
    {
        { "A.1 add an object member", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux"}]""", """{"baz":"qux","foo":"bar"}""" },
        { "A.2 add an array element", """{"foo":["bar","baz"]}""", """[{"op":"add","path":"/foo/1","value":"qux"}]""", """{"foo":["bar","qux","baz"]}""" },
        { "A.3 remove an object member", """{"baz":"qux","foo":"bar"}""", """[{"op":"remove","path":"/baz"}]""", """{"foo":"bar"}""" },
        { "A.4 remove an array element", """{"foo":["bar","qux","baz"]}""", """[{"op":"remove","path":"/foo/1"}]""", """{"foo":["bar","baz"]}""" },
        { "A.5 replace a value", """{"baz":"qux","foo":"bar"}""", """[{"op":"replace","path":"/baz","value":"boo"}]""", """{"baz":"boo","foo":"bar"}""" },
        {
            "A.6 move a value",
            """{"foo":{"bar":"baz","waldo":"fred"},"qux":{"corge":"grault"}}""",
            """[{"op":"move","from":"/foo/waldo","path":"/qux/thud"}]""",
            """{"foo":{"bar":"baz"},"qux":{"corge":"grault","thud":"fred"}}"""
        },
        { "A.7 move an array element", """{"foo":["all","grass","cows","eat"]}""", """[{"op":"move","from":"/foo/1","path":"/foo/3"}]""", """{"foo":["all","cows","eat","grass"]}""" },
        {
            "A.8 test a value (success)",
            """{"baz":"qux","foo":["a",2,"c"]}""",
            """[{"op":"test","path":"/baz","value":"qux"},{"op":"test","path":"/foo/1","value":2}]""",
            """{"baz":"qux","foo":["a",2,"c"]}"""
        },
        { "A.10 add a nested member object", """{"foo":"bar"}""", """[{"op":"add","path":"/child","value":{"grandchild":{}}}]""", """{"foo":"bar","child":{"grandchild":{}}}""" },
        { "A.11 ignore unrecognized elements", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux","xyz":123}]""", """{"foo":"bar","baz":"qux"}""" },
        { "A.14 ~ escape ordering", """{"/":9,"~1":10}""", """[{"op":"test","path":"/~01","value":10}]""", """{"/":9,"~1":10}""" },
        { "A.16 add an array value", """{"foo":["bar"]}""", """[{"op":"add","path":"/foo/-","value":["abc","def"]}]""", """{"foo":["bar",["abc","def"]]}""" }
    };

    [Theory]
    [MemberData(nameof(AppendixSuccesses))]
    public void Applies_the_rfc_6902_appendix_examples(string example, string document, string patch, string expected)
    {
        _ = example;
        AssertJsonEqual(expected, Apply(document, patch));
    }

    /// <summary>RFC 6902 Appendix A — the examples that must fail, and how.</summary>
    public static TheoryData<string, string, string, JsonPatchErrorKind> AppendixFailures => new()
    {
        { "A.9 test a value (error)", """{"baz":"qux"}""", """[{"op":"test","path":"/baz","value":"bar"}]""", JsonPatchErrorKind.TestFailed },
        { "A.12 add to a nonexistent target", """{"foo":"bar"}""", """[{"op":"add","path":"/baz/bat","value":"qux"}]""", JsonPatchErrorKind.PathNotFound },
        { "A.13 invalid patch document (duplicate op)", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux","op":"remove"}]""", JsonPatchErrorKind.Malformed },
        { "A.15 comparing strings and numbers", """{"/":9,"~1":10}""", """[{"op":"test","path":"/~01","value":"10"}]""", JsonPatchErrorKind.TestFailed }
    };

    [Theory]
    [MemberData(nameof(AppendixFailures))]
    public void Fails_the_rfc_6902_appendix_error_examples(string example, string document, string patch, JsonPatchErrorKind kind)
    {
        _ = example;
        var error = Assert.Throws<JsonPatchException>(() => Apply(document, patch));
        Assert.Equal(kind, error.Kind);
    }

    /// <summary>Cases from the json-patch-tests suite (tests.json / spec_tests.json) that should succeed.</summary>
    public static TheoryData<string, string, string> SuiteSuccesses => new()
    {
        { "{}", "[]", "{}" },
        { """{"foo":1}""", """[{"op":"add","path":"","value":{}}]""", "{}" },
        { "[]", """[{"op":"add","path":"/-","value":"hi"}]""", """["hi"]""" },
        { "[]", """[{"op":"add","path":"/0","value":"foo"}]""", """["foo"]""" },
        { """{"foo":1}""", """[{"op":"add","path":"/bar","value":null}]""", """{"foo":1,"bar":null}""" },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"/foo","value":null}]""", """{"foo":null}""" },
        { """{"foo":null}""", """[{"op":"test","path":"/foo","value":null}]""", """{"foo":null}""" },
        { """{"foo":"bar"}""", """[{"op":"add","path":"/FOO","value":"BAR"}]""", """{"foo":"bar","FOO":"BAR"}""" },
        { """{"foo":1,"bar":[1,2,3,4]}""", """[{"op":"remove","path":"/bar"}]""", """{"foo":1}""" },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"","value":{"baz":"qux"}}]""", """{"baz":"qux"}""" },
        { """{"foo":{"foo":1,"bar":2}}""", """[{"op":"test","path":"/foo","value":{"bar":2,"foo":1}}]""", """{"foo":{"foo":1,"bar":2}}""" },
        { """{"foo":[1,2]}""", """[{"op":"test","path":"/foo","value":[1,2]}]""", """{"foo":[1,2]}""" },
        { """{"1e0":"foo"}""", """[{"op":"test","path":"/1e0","value":"foo"}]""", """{"1e0":"foo"}""" },
        { """{"":1}""", """[{"op":"test","path":"/","value":1}]""", """{"":1}""" },
        { """{"foo":1}""", """[{"op":"move","from":"/foo","path":"/foo"}]""", """{"foo":1}""" },
        { """{"foo":1,"baz":[{"qux":"hello"}]}""", """[{"op":"move","from":"/foo","path":"/bar"}]""", """{"baz":[{"qux":"hello"}],"bar":1}""" },
        { """{"baz":[{"qux":"hello"}],"bar":1}""", """[{"op":"move","from":"/baz/0/qux","path":"/baz/1"}]""", """{"baz":[{},"hello"],"bar":1}""" },
        { """{"baz":[{"qux":"hello"}],"bar":1}""", """[{"op":"copy","from":"/baz/0","path":"/boo"}]""", """{"baz":[{"qux":"hello"}],"bar":1,"boo":{"qux":"hello"}}""" },
        { """{"foo":["bar"]}""", """[{"op":"copy","from":"/foo","path":"/foo/-"}]""", """{"foo":["bar",["bar"]]}""" },
        { """{"foo":[1,2]}""", """[{"op":"add","path":"/foo/2","value":3}]""", """{"foo":[1,2,3]}""" },
        { """{"foo":1}""", """[{"op":"test","path":"/foo","value":1.0}]""", """{"foo":1}""" },
        { """{"foo":100}""", """[{"op":"test","path":"/foo","value":1e2}]""", """{"foo":100}""" },
        { """{"foo":"A"}""", """[{"op":"test","path":"/foo","value":"A"}]""", """{"foo":"A"}""" },
        { """[1,2,[3,[4,5]]]""", """[{"op":"replace","path":"/2/1/0","value":"x"}]""", """[1,2,[3,["x",5]]]""" },
        { """{"foo":{"bar":1}}""", """[{"op":"move","from":"/foo/bar","path":""}]""", "1" }
    };

    [Theory]
    [MemberData(nameof(SuiteSuccesses))]
    public void Passes_the_json_patch_suite_success_cases(string document, string patch, string expected)
        => AssertJsonEqual(expected, Apply(document, patch));

    /// <summary>Cases from the json-patch-tests suite that must be refused, and the kind of refusal.</summary>
    public static TheoryData<string, string, JsonPatchErrorKind> SuiteFailures => new()
    {
        { """{"foo":["bar"]}""", """[{"op":"add","path":"/foo/01","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"bar":[1,2]}""", """[{"op":"add","path":"/bar/8","value":"5"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"bar":[1,2]}""", """[{"op":"add","path":"/bar/-1","value":"5"}]""", JsonPatchErrorKind.PathNotFound },
        { """["foo","sil"]""", """[{"op":"add","path":"/bar","value":42}]""", JsonPatchErrorKind.PathNotFound },
        { """["foo","bar"]""", """[{"op":"test","path":"/00","value":"foo"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1,"baz":[{"qux":"hello"}]}""", """[{"op":"remove","path":"/baz/1e0"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"remove","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"remove","path":"/missing1/missing2"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"add","path":"/foo/bar","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"/baz","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":[1]}""", """[{"op":"remove","path":"/foo/-"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"copy","from":"/bar","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"move","from":"/bar","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"test","path":"/foo","value":"1"}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":{"bar":[1,2,5,4]}}""", """[{"op":"test","path":"/foo","value":{"bar":[1,2]}}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":[1,2]}""", """[{"op":"test","path":"/foo","value":[2,1]}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":null}""", """[{"op":"test","path":"/foo","value":false}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":1}""", """[{"op":"add","path":"/bar"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"copy","path":"/bar"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"spam","path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"Add","path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":"foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":1,"value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":{"bar":1}}""", """[{"op":"move","from":"/foo","path":"/foo/bar/baz"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """{"op":"add","path":"/foo","value":1}""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[1]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"remove","path":""}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":"/a","value":1""", JsonPatchErrorKind.Malformed }
    };

    [Theory]
    [MemberData(nameof(SuiteFailures))]
    public void Refuses_the_json_patch_suite_error_cases(string document, string patch, JsonPatchErrorKind kind)
    {
        var error = Assert.Throws<JsonPatchException>(() => Apply(document, patch));
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public void Is_atomic_a_late_failure_leaves_the_document_untouched()
    {
        var document = JsonNode.Parse("""{"a":1,"list":[1,2]}""")!;
        var patch = JsonPatchDocument.Parse("""
            [
              {"op":"add","path":"/b","value":2},
              {"op":"remove","path":"/list/0"},
              {"op":"test","path":"/a","value":99}
            ]
            """);

        var error = Assert.Throws<JsonPatchException>(() => patch.ApplyTo(document));

        Assert.Equal(2, error.OperationIndex);
        Assert.Equal(JsonPatchOperationType.Test, error.Operation!.Op);
        AssertJsonEqual("""{"a":1,"list":[1,2]}""", document);
    }

    [Fact]
    public void Never_modifies_the_input_even_on_success()
    {
        var document = JsonNode.Parse("""{"a":1}""")!;
        var patched = JsonPatchDocument.Parse("""[{"op":"replace","path":"/a","value":2}]""").ApplyTo(document);

        AssertJsonEqual("""{"a":1}""", document);
        AssertJsonEqual("""{"a":2}""", patched);
    }

    [Fact]
    public void Can_be_applied_twice_without_the_operations_sharing_nodes()
    {
        var patch = JsonPatchDocument.Parse("""[{"op":"add","path":"/child","value":{"x":1}}]""");

        var first = patch.ApplyTo(JsonNode.Parse("{}"))!;
        var second = patch.ApplyTo(JsonNode.Parse("{}"))!;

        first["child"]!["x"] = 99;

        AssertJsonEqual("""{"child":{"x":1}}""", second);
    }

    [Fact]
    public void Names_the_failing_operation_when_parsing()
    {
        var error = Assert.Throws<JsonPatchException>(() => JsonPatchDocument.Parse("""
            [
              {"op":"add","path":"/a","value":1},
              {"op":"frobnicate","path":"/a"}
            ]
            """));

        Assert.Equal(JsonPatchErrorKind.Malformed, error.Kind);
        Assert.Equal(1, error.OperationIndex);
        Assert.Contains("frobnicate", error.Message);
    }

    [Fact]
    public void Builds_and_round_trips_a_patch_in_code()
    {
        var patch = new JsonPatchDocument()
            .Test("/version", 3)
            .Replace("/name", "new")
            .Add("/tags/-", "x")
            .Remove("/notes")
            .Move("/a", "/b")
            .Copy("/b", "/c")
            .Add("/nothing", null);

        var json = patch.ToJsonString();
        var reparsed = JsonPatchDocument.Parse(json);

        Assert.Equal(7, reparsed.Operations.Count);
        Assert.Equal(json, reparsed.ToJsonString());
        Assert.Contains("""{"op":"add","path":"/nothing","value":null}""", json);
        Assert.Contains("""{"op":"move","from":"/a","path":"/b"}""", json);
    }

    [Fact]
    public void Refuses_a_move_into_its_own_child_while_building()
    {
        var error = Assert.Throws<JsonPatchException>(() => new JsonPatchDocument().Add("/x", 1).Move("/a", "/a/b"));

        Assert.Equal(JsonPatchErrorKind.Malformed, error.Kind);
        Assert.Equal(1, error.OperationIndex);
    }

    [Fact]
    public void Applies_to_a_typed_value_through_its_json_metadata()
    {
        var widget = new PatchWidget(1, "old", ["a"], 10m);
        var patch = JsonPatchDocument.Parse("""
            [
              {"op":"test","path":"/price","value":10.00},
              {"op":"replace","path":"/name","value":"new"},
              {"op":"add","path":"/tags/-","value":"b"}
            ]
            """);

        var patched = patch.ApplyTo(widget, PatchJson.Default.PatchWidget);

        Assert.Equal("new", patched.Name);
        Assert.Equal(["a", "b"], patched.Tags);
        Assert.Equal("old", widget.Name);
    }

    [Fact]
    public void Reports_a_result_that_no_longer_fits_the_type_as_unprocessable()
    {
        var patch = JsonPatchDocument.Parse("""[{"op":"replace","path":"/id","value":"not-a-number"}]""");

        var error = Assert.Throws<JsonPatchException>(() =>
            patch.ApplyTo(new PatchWidget(1, "n", [], 1m), PatchJson.Default.PatchWidget));

        Assert.Equal(JsonPatchErrorKind.InvalidResult, error.Kind);
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public void Round_trips_through_an_apps_own_serializer_context()
    {
        var patch = new JsonPatchDocument().Replace("/a", 1);

        var json = JsonSerializer.Serialize(patch, JsonPatchContext.Default.JsonPatchDocument);
        var back = JsonSerializer.Deserialize(json, JsonPatchContext.Default.JsonPatchDocument)!;

        Assert.Equal(patch.ToJsonString(), back.ToJsonString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""[{"op":"nope"}]""", JsonPatchContext.Default.JsonPatchDocument));
    }

    [Theory]
    [InlineData(JsonPatchErrorKind.Malformed, 400)]
    [InlineData(JsonPatchErrorKind.PathNotFound, 409)]
    [InlineData(JsonPatchErrorKind.TestFailed, 409)]
    [InlineData(JsonPatchErrorKind.InvalidResult, 422)]
    [InlineData(JsonPatchErrorKind.UnsupportedMediaType, 415)]
    public void Maps_each_failure_to_its_rfc_5789_status(JsonPatchErrorKind kind, int status)
    {
        var error = new JsonPatchException(kind, "x");

        Assert.Equal(status, error.StatusCode);

        // And it is the BadHttpRequestException status ASP.NET honours when the exception escapes a handler.
        Assert.Equal(status, Assert.IsAssignableFrom<BadHttpRequestException>(error).StatusCode);
        Assert.Equal(status, error.ToProblemDetails().Status);
    }
}

[JsonSerializable(typeof(JsonPatchDocument))]
public partial class JsonPatchContext : JsonSerializerContext;
