using System;
using System.Collections.Generic;
using System.Text;
using Zep.Core.FileOperations;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.FileOperations
{
    // JsonText replaced JsonUtility for the package's two machine-owned JSON
    // files. These pin the JSON subset those files rely on, the escapes the
    // writer emits, and that malformed text is an error rather than a repaired
    // document — a persistence boundary must say when its input was invalid.
    public sealed class JsonTextTests
    {
        [Test]
        public void Parse_ReadsNestedObjectsArraysAndScalars()
        {
            var value = JsonText.Parse(
                " {\"Generation\": 12, \"Changes\": {\"Imported\": [\"a\", \"b\"], \"Deleted\": []},"
                + " \"flag\": true, \"none\": null, \"ratio\": -1.5e2, \"Batches\": [ {\"Moved\": [\"x\"]} ] } ");

            var root = JsonText.AsObject(value);
            Assert.That(root, Is.Not.Null);
            Assert.That(JsonText.TryGetInt64(root["Generation"], out var generation), Is.True);
            Assert.That(generation, Is.EqualTo(12));
            Assert.That(JsonText.ReadStringArray(root["Changes"], "Imported"), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(JsonText.ReadStringArray(root["Changes"], "Deleted"), Is.Empty);
            Assert.That(JsonText.ReadStringArray(root["Changes"], "Missing"), Is.Empty, "an absent member reads as empty");
            Assert.That(root["flag"], Is.True);
            Assert.That(root["none"], Is.Null);
            Assert.That(root["ratio"], Is.EqualTo(-150d));
            var batches = JsonText.AsArray(root["Batches"]);
            Assert.That(batches.Count, Is.EqualTo(1));
            Assert.That(JsonText.ReadStringArray(batches[0], "Moved"), Is.EqualTo(new[] { "x" }));
        }

        [Test]
        public void Parse_DecodesEveryEscape()
        {
            var value = JsonText.Parse("\"q\\\"b\\\\s\\/c\\b\\f\\n\\r\\t u\\u00e9 s\\ud83d\\ude00\"");
            Assert.That(value, Is.EqualTo("q\"b\\s/c\b\f\n\r\t ué s😀"));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{")]
        [TestCase("{\"a\":1,}")]
        [TestCase("[1 2]")]
        [TestCase("{\"a\" 1}")]
        [TestCase("\"open")]
        [TestCase("\"bad\\x\"")]
        [TestCase("tru")]
        [TestCase("{\"a\":1} trailing")]
        [TestCase("<<<<<<< HEAD\n{\"assignments\":[]}\n=======\n")]
        public void Parse_RejectsMalformedText(string text)
        {
            Assert.Throws<FormatException>(() => JsonText.Parse(text));
        }

        [Test]
        public void AppendString_EscapesWhatTheParserDecodes()
        {
            var original = "plain \"quoted\" back\\slash\ttab\nnewlinecontrol é 😀";
            var builder = new StringBuilder();
            JsonText.AppendString(builder, original);
            var json = builder.ToString();

            Assert.That(json, Does.Not.Contain("\n"), "control characters must be escaped, not written raw");
            Assert.That(JsonText.Parse(json), Is.EqualTo(original));
        }

        [Test]
        public void AppendStringArray_RoundTrips()
        {
            var builder = new StringBuilder();
            JsonText.AppendStringArray(builder, new List<string> { "Assets/A.png", "Assets/B \"C\".mat" });
            var parsed = JsonText.AsArray(JsonText.Parse(builder.ToString()));
            Assert.That(parsed, Is.EqualTo(new object[] { "Assets/A.png", "Assets/B \"C\".mat" }));
        }
    }
}
