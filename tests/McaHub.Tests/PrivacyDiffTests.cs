namespace McaHub.Tests;

/// <summary>
/// #34 regression: the commit/compare diff views must redact player PII (positions, inventory, sign text)
/// from non-collaborators on a public world — the same gate the world explorer uses. The world explorer
/// enforced it but the diff renderer didn't, leaking coordinates/inventory to anonymous viewers. These
/// assert the redaction predicate that decides what `RenderDiff` hides when canSeeData is false; block /
/// biome changes and the grief summary stay public (the headline feature) and must never be redacted.
/// </summary>
public class PrivacyDiffTests
{
    [Theory]
    [InlineData("level.dat")]
    [InlineData("level.dat_old")]
    [InlineData("playerdata/8f4e-…-uuid.dat")]
    [InlineData("entities/r.0.0.mca")]
    public void Player_data_files_are_sensitive(string rel) => Assert.True(Pages.SensitiveFile(rel));

    [Theory]
    [InlineData("region/r.0.0.mca")]   // block changes (grief) — public
    [InlineData("poi/r.0.0.mca")]
    [InlineData("data/scoreboard.dat")]
    public void Block_and_other_files_are_not_file_sensitive(string rel) => Assert.False(Pages.SensitiveFile(rel));

    [Theory]
    [InlineData("block_entities[2,-48,15].Items[0].id")]   // chest inventory + coords
    [InlineData("block_entities[10,64,10].front_text.messages[0]")] // sign text
    [InlineData("Entities[uuid].Pos[0]")]                  // entity position (legacy in-chunk)
    [InlineData("block_entity.CustomName")]
    public void Container_sign_entity_paths_are_sensitive(string path) => Assert.True(Pages.SensitivePath(path));

    [Theory]
    [InlineData("sections[3].block_states[@1,2,3]")]       // a griefed block — must stay public
    [InlineData("sections[-1].biomes[@0,0,0]")]
    [InlineData("Heightmaps.WORLD_SURFACE")]
    [InlineData("InhabitedTime")]
    public void Block_grief_paths_are_never_redacted(string path) => Assert.False(Pages.SensitivePath(path));
}
