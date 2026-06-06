namespace McadiffHub.Tests;

/// <summary>
/// The repo-name path-traversal guard (#20). `RepoStore.IsValidName` is the single gate before any
/// filesystem path is built from a user-supplied name, so its accept/reject set is a trust boundary.
/// </summary>
public class IsValidNameTests
{
    [Theory]
    [InlineData("world")]
    [InlineData("my-world")]
    [InlineData("a")]
    [InlineData("World_1.2")]
    [InlineData("a.b-c_d")]
    public void Accepts_safe_names(string name) => Assert.True(RepoStore.IsValidName(name));

    [Fact]
    public void Accepts_a_64_char_name() => Assert.True(RepoStore.IsValidName(new string('a', 64)));

    [Theory]
    [InlineData("")]            // empty
    [InlineData("..")]          // parent dir
    [InlineData("a/b")]         // path separator
    [InlineData("a\\b")]        // windows separator
    [InlineData("../etc")]      // traversal
    [InlineData("-x")]          // leading dash
    [InlineData(".x")]          // leading dot
    [InlineData("a b")]         // space
    [InlineData("a\0b")]        // NUL
    [InlineData("ｗorld")]       // fullwidth lookalike (non-ASCII)
    public void Rejects_traversal_and_unsafe_names(string name) => Assert.False(RepoStore.IsValidName(name));

    [Fact]
    public void Rejects_a_65_char_name() => Assert.False(RepoStore.IsValidName(new string('a', 65)));
}
