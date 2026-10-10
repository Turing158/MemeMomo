namespace MemeMomo.Models;

internal sealed record GitHubRelease(string TagName, Uri HtmlUrl)
{
    public bool MatchesVersion(string version)
    {
        string tagVersion = TagName.StartsWith('v') || TagName.StartsWith('V') ? TagName[1..] : TagName;
        return string.Equals(tagVersion, version, StringComparison.Ordinal);
    }
}
