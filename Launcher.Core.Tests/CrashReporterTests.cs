using Cyanlabs.Launcher.Updating;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cyanlabs.Launcher.Updating.Tests
{
    public class CrashReporterTests
    {
        [Fact]
        public void Dsn_becomes_the_store_url_and_key()
        {
            CrashDsn d = CrashDsn.TryParse("https://abc123@glitchtip.example.net/7");
            Assert.NotNull(d);
            Assert.Equal("abc123", d.PublicKey);
            Assert.Equal("https://glitchtip.example.net/api/7/store/", d.StoreUrl.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("https://host/7")]
        [InlineData("ftp://k@host/7")]
        public void Bad_dsn_is_none(string dsn) => Assert.Null(CrashDsn.TryParse(dsn));

        [Fact]
        public void Home_folder_is_scrubbed_in_either_slash_style()
        {
            Assert.Equal("at ~\\x.cs and ~/y.cs", CrashReporter.Scrub(@"at C:\Users\bob\x.cs and C:/Users/bob/y.cs", @"C:\Users\bob"));
        }

        [Fact]
        public void Event_has_frames_oldest_first_and_no_machine_name()
        {
            string trace = "   at Cyanlabs.Launcher.B() in C:\\Users\\bob\\src\\B.cs:line 9\n   at Cyanlabs.Launcher.A() in C:\\src\\A.cs:line 3";
            JObject ev = JObject.Parse(CrashReporter.BuildEventJson("update-failed", "T", "m", trace, "2.14.2.2", "Windows build 19045", "x64", "id1"));
            Assert.Equal("syn3launcher@2.14.2.2", (string)ev["release"]);
            Assert.Equal("", (string)ev["server_name"]);
            JArray frames = (JArray)ev["exception"]["values"][0]["stacktrace"]["frames"];
            Assert.Equal("Cyanlabs.Launcher.A()", (string)frames[0]["function"]);
            Assert.Equal(9, (int)frames[1]["lineno"]);
        }
    }
}
