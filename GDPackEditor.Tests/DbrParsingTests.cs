using Xunit;

namespace GDPackEditor.Tests
{
    public class DbrParsingTests
    {
        [Fact]
        public void ParseDbrFields_HandlesTrailingCommasAndTags()
        {
            const string text =
                "itemNameTag,records/items/foo,\r\n" +
                "skillDisplayName,Test Skill,\r\n" +
                "fileDescription,A thing,\r\n";

            var result = DbrHelper.ParseDbrFields(text, "records/items/foo.dbr");

            Assert.Equal("records/items/foo.dbr", result.RecordName);
            Assert.Equal("A thing", result.Description);
            Assert.Equal("records/items/foo", result.FirstTagValue);
            Assert.True(result.HasTags);
        }

        [Fact]
        public void GetFieldValue_TrimsValueAndTrailingComma()
        {
            const string text = "skillDisplayName,Test Skill,\r\n";

            Assert.Equal("Test Skill", DbrHelper.GetFieldValue(text, "skillDisplayName"));
            Assert.Equal("", DbrHelper.GetFieldValue(text, "missingField"));
        }
    }
}
